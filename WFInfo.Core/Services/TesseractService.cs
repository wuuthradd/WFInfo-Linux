using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

using Tesseract;
using WFInfo.Settings;
using WFInfo.LanguageProcessing;

namespace WFInfo.Services
{
    public class TesseractService : ITesseractService, IDisposable
    {
        public TesseractEngine FirstEngine { get; private set; }
        public TesseractEngine[] Engines { get; } = new TesseractEngine[4];
        public TesseractEngine NumbersOnlyEngine { get; private set; }

        private readonly IReadOnlyApplicationSettings _settings;
        private string Locale => _settings.Locale;
        private string DataPath;

        private const string DefaultWhitelist = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const string NumbersOnlyWhitelist = "0123456789";

        public TesseractService(IReadOnlyApplicationSettings settings)
        {
            _settings = settings;
            string tessPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
            if (!string.IsNullOrEmpty(tessPrefix) && Directory.Exists(tessPrefix))
                DataPath = tessPrefix;
            else
                DataPath = PlatformPaths.TessDataPath;
            Directory.CreateDirectory(DataPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                SetupLinuxNativeLibs();
        }

        private TesseractEngine CreateEngine()
        {
            var engine = new TesseractEngine(DataPath, Locale);

            engine.SetVariable("tessedit_zero_rejection", "false");
            engine.SetVariable("tessedit_write_rep_codes", "false");
            engine.SetVariable("tessedit_write_unlv", "false");
            engine.SetVariable("tessedit_fix_fuzzy_spaces", "true");
            engine.SetVariable("tessedit_prefer_joined_broken", "false");
            engine.SetVariable("preserve_interword_spaces", "1");
            engine.SetVariable("language_model_penalty_case_ok", "0.1");
            engine.SetVariable("language_model_penalty_case_bad", "0.4");
            engine.SetVariable("thresholding_method", "0");

            if (Locale == "ko" || Locale == "zh-hans" || Locale == "zh-hant")
            {
                engine.SetVariable("textord_noise_normratio", "2.0");
                engine.SetVariable("chop_enable", "0");
                engine.SetVariable("use_new_state_cost", "1");
                engine.SetVariable("load_system_dawg", "true");
                engine.SetVariable("load_freq_dawg", "true");
                engine.SetVariable("language_model_penalty_non_dict_word", "0");
                engine.SetVariable("user_defined_dpi", "300");
                engine.SetVariable("segment_nonalphabetic_script", "1");
            }
            else if (Locale == "en")
            {
                engine.SetVariable("load_system_dawg", "false");
                engine.SetVariable("load_freq_dawg", "false");
                engine.SetVariable("user_defined_dpi", "300");
                engine.SetVariable("textord_noise_normratio", "1.0");
            }

            string whitelist;
            try
            {
                var processor = LanguageProcessorFactory.GetProcessor(Locale);
                whitelist = processor?.CharacterWhitelist ?? DefaultWhitelist;
            }
            catch (InvalidOperationException)
            {
                whitelist = DefaultWhitelist;
            }
            engine.SetVariable("tessedit_char_whitelist", whitelist);

            return engine;
        }

        private TesseractEngine CreateNumbersOnlyEngine()
        {
            var engine = new TesseractEngine(DataPath, Locale);
            engine.SetVariable("tessedit_char_whitelist", NumbersOnlyWhitelist);
            engine.SetVariable("tessedit_zero_rejection", "false");
            engine.SetVariable("preserve_interword_spaces", "0");
            return engine;
        }

        public void Init()
        {
            DownloadTessdata();
            try
            {
                LoadEngines();
                FirstEngine?.Dispose();
                FirstEngine = CreateEngine();
                NumbersOnlyEngine?.Dispose();
                NumbersOnlyEngine = CreateNumbersOnlyEngine();
                AppMain.AddLog("TesseractService.Init() completed successfully");
            }
            catch (TesseractException ex) when (TryFallbackDataPath())
            {
                // Retry with ASCII-safe fallback path (non-ASCII home dirs break Tesseract)
                AppMain.AddLog($"TesseractService: primary path failed ({ex.Message}), retrying with fallback: {DataPath}");
                DownloadTessdata();
                LoadEngines();
                FirstEngine?.Dispose();
                FirstEngine = CreateEngine();
                NumbersOnlyEngine?.Dispose();
                NumbersOnlyEngine = CreateNumbersOnlyEngine();
                AppMain.AddLog("TesseractService.Init() succeeded with fallback path");
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"TesseractService.Init() failed: {ex}");
                throw;
            }
        }

        private bool TryFallbackDataPath()
        {
            string fallback = Path.Combine(Path.GetTempPath(), "WFInfo", "tessdata");
            if (DataPath == fallback)
                return false;  // already tried fallback
            DataPath = fallback;
            Directory.CreateDirectory(DataPath);
            return true;
        }

        // P/Invoke to libdl for explicit library preloading
        private const int RTLD_NOW = 0x002;
        private const int RTLD_GLOBAL = 0x100;

        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        private static extern IntPtr dlerror();

        /// <summary>
        /// On Linux, the Tesseract NuGet package's InteropDotNet loader looks for
        /// native libs as: {exeDir}/x64/lib{name}.so and {exeDir}/lib{name}.so
        /// But the names don't match system libs (e.g. libleptonica-1.82.0.so vs libleptonica.so.6).
        /// We create symlinks in all directories InteropDotNet searches and pre-load them.
        /// </summary>
        private static readonly string[] LibDirs =
            { "/usr/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/lib/x86_64-linux-gnu" };

        private static string[] FindNativeLib(string baseName)
        {
            var candidates = new List<string>();
            string pattern = baseName + ".so*";
            string prefix = baseName + ".so";
            foreach (string dir in LibDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(dir, pattern))
                    {
                        string name = Path.GetFileName(f);
                        if (name == prefix || name.StartsWith(prefix + "."))
                            candidates.Add(f);
                    }
                }
                catch { }
            }
            candidates.Sort((a, b) => b.Length.CompareTo(a.Length));
            return candidates.ToArray();
        }

        private static bool _resolverRegistered;

        private void SetupLinuxNativeLibs()
        {
            if (!_resolverRegistered)
            {
                _resolverRegistered = true;
                NativeLibrary.SetDllImportResolver(typeof(Tesseract.TesseractEngine).Assembly,
                    (name, asm, paths) =>
                    {
                        if (name == "libdl" || name == "libdl.so")
                        {
                            if (NativeLibrary.TryLoad("libdl.so.2", out IntPtr handle))
                                return handle;
                        }
                        return IntPtr.Zero;
                    });
            }

            string exeDir = AppContext.BaseDirectory;

            // Check if exe directory is writable (AppImage mounts read-only SquashFS)
            bool exeDirWritable = false;
            try
            {
                string testFile = Path.Combine(exeDir, ".wfinfo-write-test");
                File.WriteAllText(testFile, "");
                File.Delete(testFile);
                exeDirWritable = true;
            }
            catch { }

            var libMappings = new (string Expected, string[] SystemPaths)[]
            {
                ("libdl.so", new[] { "/usr/lib/libdl.so.2", "/usr/lib/x86_64-linux-gnu/libdl.so.2", "/lib/x86_64-linux-gnu/libdl.so.2", "/usr/lib64/libdl.so.2" }),
                ("libleptonica-1.82.0.so", FindNativeLib("libleptonica")),
                ("libtesseract50.so", FindNativeLib("libtesseract")),
            };

            if (!exeDirWritable)
            {
                // Read-only filesystem (AppImage). InteropDotNet checks File.Exists before
                // dlopen so LD_LIBRARY_PATH alone won't work. Create symlinks in a writable
                // directory and point InteropDotNet's CustomSearchPath there.
                string nativeDir = Path.Combine(PlatformPaths.AppDataPath, "native");
                string nativeX64 = Path.Combine(nativeDir, "x64");
                Directory.CreateDirectory(nativeX64);

                foreach (var (expected, systemPaths) in libMappings)
                {
                    string systemLib = null;
                    foreach (string candidate in systemPaths)
                    {
                        if (File.Exists(candidate)) { systemLib = candidate; break; }
                    }
                    if (systemLib == null) continue;

                    string symlinkPath = Path.Combine(nativeX64, expected);
                    try
                    {
                        if (File.Exists(symlinkPath)) File.Delete(symlinkPath);
                        File.CreateSymbolicLink(symlinkPath, systemLib);
                    }
                    catch { try { File.Copy(systemLib, symlinkPath, true); } catch { } }

                    try
                    {
                        IntPtr handle = dlopen(systemLib, RTLD_NOW | RTLD_GLOBAL);
                        if (handle != IntPtr.Zero)
                            AppMain.AddLog($"Pre-loaded native lib: {expected} ({systemLib})");
                    }
                    catch { }
                }

                InteropDotNet.LibraryLoader.Instance.CustomSearchPath = nativeDir;
                AppMain.AddLog($"InteropDotNet custom search path set to {nativeDir}");
                return;
            }

            // Writable filesystem (tarball, dotnet run) - create symlinks in exe directory
            string x64 = Path.Combine(exeDir, "x64");
            Directory.CreateDirectory(x64);
            foreach (var (expected, systemPaths) in libMappings)
            {
                string systemLib = null;
                foreach (string candidate in systemPaths)
                {
                    if (File.Exists(candidate)) { systemLib = candidate; break; }
                }
                if (systemLib == null) continue;

                foreach (string dir in new[] { exeDir, x64 })
                {
                    string symlinkPath = Path.Combine(dir, expected);
                    if (!File.Exists(symlinkPath))
                    {
                        try { File.CreateSymbolicLink(symlinkPath, systemLib); }
                        catch { try { File.Copy(systemLib, symlinkPath, true); } catch { } }
                    }
                }

                string fullPath = Path.Combine(exeDir, expected);
                if (File.Exists(fullPath))
                {
                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        handle = dlopen(fullPath, RTLD_NOW | RTLD_GLOBAL);
                    }
                    catch (DllNotFoundException)
                    {
                        NativeLibrary.TryLoad(fullPath, out handle);
                    }
                    if (handle != IntPtr.Zero)
                        AppMain.AddLog($"Pre-loaded native lib: {expected}");
                }
            }
        }

        private void LoadEngines()
        {
            for (var i = 0; i < 4; i++)
            {
                Engines[i]?.Dispose();
                Engines[i] = CreateEngine();
            }
        }

        public void ReloadEngines()
        {
            DownloadTessdata();
            LoadEngines();
            FirstEngine?.Dispose();
            FirstEngine = CreateEngine();
            NumbersOnlyEngine?.Dispose();
            NumbersOnlyEngine = CreateNumbersOnlyEngine();
        }

        private void DownloadTessdata()
        {
            string traineddata_hotlink_prefix = "https://raw.githubusercontent.com/WFCD/WFinfo/libs/tessdata/";
            var traineddata_checksums = new Dictionary<string, string>
            {
                {"en", "7af2ad02d11702c7092a5f8dd044d52f"},
                {"ko", "c776744205668b7e76b190cc648765da"},
                {"fr", "ac0a3da6bf50ed0dab61b46415e82c17"},
                {"uk", "fe1312cbfb602fc179796dbf54ee65fe"},
                {"it", "401cd425084217b224f99c3f55c78518"},
                {"de", "d37aac5fce1c7d8f279a42f076c935d8"},
                {"es", "130215a6355e9ea651f483279271d354"},
                {"pt", "9627fa0ccecdc9dfdb9ac232bbbd744f"},
                {"pl", "33bb3c504011b839cf6e2b689ea68578"},
                {"ru", "2e2022eddce032b754300a8188b41419"},
                {"zh-hans", "921bdf9c27a17ce5c7c77c10345ad8fb"},
                {"zh-hant", "5865dded9ef6d035c165fb14317f1402"},
            };

            if (!traineddata_checksums.TryGetValue(Locale, out string expectedChecksum))
            {
                AppMain.AddLog($"Unsupported locale '{Locale}', no traineddata checksum, skipping download");
                return;
            }

            string traineddata_hotlink = traineddata_hotlink_prefix + Locale + ".traineddata";
            string localPath = Path.Combine(DataPath, Locale + ".traineddata");

            if (File.Exists(localPath) && GetMD5hash(localPath) == expectedChecksum)
                return;

            try
            {
                AppMain.AddLog($"Downloading tessdata for locale '{Locale}'...");
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);
                var data = client.GetByteArrayAsync(traineddata_hotlink).GetAwaiter().GetResult();
                File.WriteAllBytes(localPath, data);
                AppMain.AddLog("Tessdata download complete");
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to download traineddata for '{Locale}': {ex.Message}");
            }
        }

        private static string GetMD5hash(string filePath)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            FirstEngine?.Dispose();
            FirstEngine = null;
            for (int i = 0; i < Engines.Length; i++)
            {
                Engines[i]?.Dispose();
                Engines[i] = null;
            }
            NumbersOnlyEngine?.Dispose();
            NumbersOnlyEngine = null;
        }
    }
}
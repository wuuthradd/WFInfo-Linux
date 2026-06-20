using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static class SelfUpdater
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/wuuthradd/WFInfo-Linux/releases";

        public static bool IsAppImage =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")) &&
            AppContext.BaseDirectory.Contains("/.mount_");

        public static async Task<string> PerformUpdate(string targetVersion)
        {
            try
            {
                AppMain.AddLog($"Self-update: starting update to {targetVersion}");

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);

                string json = await client.GetStringAsync(ReleasesApiUrl);
                var releases = JArray.Parse(json);

                string assetUrl = null;
                string assetName = IsAppImage ? "WFInfo.AppImage" : "WFInfo-linux-x64.tar.gz";

                foreach (JObject release in releases)
                {
                    string tag = release["tag_name"]?.ToString()?.TrimStart('v');
                    if (tag != targetVersion) continue;

                    if (release["assets"] is JArray assets)
                    {
                        foreach (JObject asset in assets)
                        {
                            if (asset["name"]?.ToString() == assetName)
                            {
                                assetUrl = asset["browser_download_url"]?.ToString();
                                break;
                            }
                        }
                    }
                    break;
                }

                if (assetUrl == null)
                    return $"Release asset '{assetName}' not found for version {targetVersion}";

                AppMain.AddLog($"Self-update: downloading {assetUrl}");

                string tempDir = Path.Combine(Path.GetTempPath(), "wfinfo-update-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
                string tempFile = Path.Combine(tempDir, assetName);

                using (var response = await client.GetAsync(assetUrl))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tempFile);
                    await response.Content.CopyToAsync(fs);
                }

                AppMain.AddLog($"Self-update: downloaded to {tempFile}");

                if (IsAppImage)
                    return InstallAppImage(tempFile, tempDir);
                else
                    return InstallTarball(tempFile, tempDir);
            }
            catch (UnauthorizedAccessException)
            {
                AppMain.AddLog("Self-update failed: permission denied");
                return "Permission denied, cannot write to install directory";
            }
            catch (HttpRequestException ex)
            {
                AppMain.AddLog($"Self-update failed: {ex.Message}");
                return "Download failed, check your internet connection";
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Self-update failed: {ex.Message}");
                return ex.Message;
            }
        }

        private static string InstallAppImage(string tempFile, string tempDir)
        {
            string appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrEmpty(appImagePath))
                return "Could not determine AppImage path";

            AppMain.AddLog($"Self-update: replacing AppImage at {appImagePath}");

            string backup = appImagePath + ".old";
            try { File.Delete(backup); } catch { }
            File.Move(appImagePath, backup);

            try
            {
                File.Move(tempFile, appImagePath);
                File.SetUnixFileMode(appImagePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                File.Move(backup, appImagePath);
                throw;
            }

            try { File.Delete(backup); } catch { }
            CleanupTemp(tempDir);

            Relaunch(appImagePath);
            return null;
        }

        private static string InstallTarball(string tempFile, string tempDir)
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string installRoot = Path.GetDirectoryName(baseDir);

            if (installRoot == null)
                return "Could not determine install directory";

            AppMain.AddLog($"Self-update: extracting tarball, install root: {installRoot}");

            // Use a staging dir on the same filesystem to avoid cross-device move errors
            string stagingDir = Path.Combine(installRoot, ".wfinfo-update");
            try { Directory.Delete(stagingDir, true); } catch { }
            Directory.CreateDirectory(stagingDir);

            string extractDir = Path.Combine(stagingDir, "extracted");
            Directory.CreateDirectory(extractDir);

            var psi = new ProcessStartInfo("tar", $"xzf \"{tempFile}\" -C \"{extractDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return "Failed to start tar process";
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return "Failed to extract update archive";
            }

            string newContentDir = Path.Combine(extractDir, "WFInfo-linux-x64");
            if (!Directory.Exists(newContentDir))
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return "Invalid update archive structure";
            }

            string backupDir = Path.Combine(stagingDir, "backup");
            Directory.CreateDirectory(backupDir);

            string libDir = Path.Combine(installRoot, "lib");
            string launcherPath = Path.Combine(installRoot, "WFInfo");

            if (Directory.Exists(libDir))
                Directory.Move(libDir, Path.Combine(backupDir, "lib"));
            if (File.Exists(launcherPath))
                File.Move(launcherPath, Path.Combine(backupDir, "WFInfo"));

            try
            {
                Directory.Move(Path.Combine(newContentDir, "lib"), libDir);
                if (File.Exists(Path.Combine(newContentDir, "WFInfo")))
                    File.Move(Path.Combine(newContentDir, "WFInfo"), launcherPath);

                File.SetUnixFileMode(launcherPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                string mainBinary = Path.Combine(libDir, "WFInfo.Linux");
                if (File.Exists(mainBinary))
                    File.SetUnixFileMode(mainBinary,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                AppMain.AddLog("Self-update: install failed, restoring backup");
                try { Directory.Delete(libDir, true); } catch { }
                try { File.Delete(launcherPath); } catch { }
                if (Directory.Exists(Path.Combine(backupDir, "lib")))
                    Directory.Move(Path.Combine(backupDir, "lib"), libDir);
                if (File.Exists(Path.Combine(backupDir, "WFInfo")))
                    File.Move(Path.Combine(backupDir, "WFInfo"), launcherPath);
                throw;
            }

            try { Directory.Delete(stagingDir, true); } catch { }
            CleanupTemp(tempDir);

            Relaunch(launcherPath);
            return null;
        }

        private static void CleanupTemp(string tempDir)
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        private static void Relaunch(string path)
        {
            AppMain.AddLog($"Self-update: relaunching from {path}");
            AppMain.FlushLog();

            // Release the single-instance lock before relaunch
            string lockPath = Path.Combine(PlatformPaths.AppDataPath, "wfinfo.lock");
            try
            {
                Program.ReleaseLock();
                File.Delete(lockPath);
            }
            catch { }

            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false
            });
            Environment.Exit(0);
        }
    }
}
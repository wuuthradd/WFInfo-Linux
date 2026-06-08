using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WFInfo.Services
{
    /// <summary>
    /// Cross-platform path resolution.
    /// Windows: %APPDATA%\WFInfo
    /// Linux: ~/.local/share/WFInfo (XDG_DATA_HOME)
    /// </summary>
    public static class PlatformPaths
    {
        private static readonly string _appDataPath = InitAppDataPath();

        private static string InitAppDataPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(xdg))
                    xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(xdg, "WFInfo");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WFInfo");
        }

        public static string AppDataPath => _appDataPath;

        public static string TessDataPath => Path.Combine(AppDataPath, "tessdata");

        /// <summary>
        /// Auto-detect Warframe EE.log path.
        /// Windows: %LOCALAPPDATA%\Warframe\EE.log
        /// Linux/Proton: derived from running process environment, then fallback to common paths.
        /// </summary>
        /// <param name="winePrefix">Optional Wine prefix from /proc/&lt;pid&gt;/environ (most reliable).</param>
        private static readonly string[] WineUsernames = new[]
        {
            "steamuser",
            Environment.UserName,
        };

        private static string EELogSuffix(string username) =>
            Path.Combine("drive_c", "users", username, "AppData", "Local", "Warframe", "EE.log");

        private static string TryUsers(string prefixPath)
        {
            foreach (string user in WineUsernames)
            {
                string path = Path.Combine(prefixPath, EELogSuffix(user));
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public static string FindEELogPath(string winePrefix = null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (!string.IsNullOrEmpty(winePrefix))
                {
                    string fromEnv = TryUsers(winePrefix);
                    if (fromEnv != null)
                        return fromEnv;
                }

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] prefixes = new[]
                {
                    Path.Combine(home, ".steam", "steam", "steamapps", "compatdata", "230410", "pfx"),
                    Path.Combine(home, ".local", "share", "Steam", "steamapps", "compatdata", "230410", "pfx"),
                    Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "steamapps", "compatdata", "230410", "pfx"),
                    Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "compatdata", "230410", "pfx"),
                    Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam", "steamapps", "compatdata", "230410", "pfx"),
                };

                foreach (string prefix in prefixes)
                {
                    string found = TryUsers(prefix);
                    if (found != null)
                        return found;
                }

                string[] vdfCandidates = new[]
                {
                    Path.Combine(home, ".steam", "steam", "steamapps", "libraryfolders.vdf"),
                    Path.Combine(home, ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
                    Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "steamapps", "libraryfolders.vdf"),
                    Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
                    Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
                };

                foreach (string vdfPath in vdfCandidates)
                {
                    if (!File.Exists(vdfPath)) continue;
                    string vdf;
                    try { vdf = File.ReadAllText(vdfPath); }
                    catch { continue; }
                    foreach (string line in vdf.Split('\n'))
                    {
                        string trimmed = line.Trim().Trim('"');
                        if (trimmed.StartsWith("path"))
                        {
                            string[] parts = line.Split('"');
                            if (parts.Length < 4) continue;
                            string libPath = parts[3];
                            string pfx = Path.Combine(libPath, "steamapps", "compatdata", "230410", "pfx");
                            string found = TryUsers(pfx);
                            if (found != null)
                                return found;
                        }
                    }
                }

                return null;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe", "EE.log");
        }
    }
}
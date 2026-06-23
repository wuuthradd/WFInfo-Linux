using System.Diagnostics;

namespace WFInfo.Services.WarframeProcess
{
    /// <summary>
    /// Used to signal game process changes.
    /// </summary>
    public delegate void ProcessChangedArgs(Process newProcess);

    /// <summary>
    /// Finds and provides handles to the game process (cross-platform).
    /// </summary>
    public interface IProcessFinder
    {
        /// <summary>
        /// Gets the game process. Null when no process found.
        /// </summary>
        Process Warframe { get; }

        /// <summary>
        /// Whether the game process is running.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Wine/Proton environment detected from the running game process.
        /// Null when no game process is running or on Windows.
        /// </summary>
        WineEnvironmentInfo WineEnvironment { get; }

        /// <summary>
        /// Invoked when the game process state changes.
        /// </summary>
        event ProcessChangedArgs OnProcessChanged;
    }

    /// <summary>
    /// Wine/Proton environment info extracted from /proc/&lt;pid&gt;/environ.
    /// Works for any Steam install (system, Flatpak, Snap, custom).
    /// </summary>
    public class WineEnvironmentInfo
    {
        /// <summary>Wine prefix path (WINEPREFIX).</summary>
        public string WinePrefix { get; set; }

        /// <summary>Steam compatdata path (STEAM_COMPAT_DATA_PATH).</summary>
        public string CompatDataPath { get; set; }

        /// <summary>Wine loader binary path (WINELOADER env var).</summary>
        public string WineLoaderPath { get; set; }

        /// <summary>STEAM_COMPAT_TOOL_PATHS env var (Proton directory path).</summary>
        public string CompatToolPaths { get; set; }

        /// <summary>Proton version string from STEAM_COMPAT_TOOL_PATHS or config_info.</summary>
        public string ProtonVersion { get; set; }

        /// <summary>Derived EE.log path from the prefix.</summary>
        public string EELogPath
        {
            get
            {
                string prefix = WinePrefix ?? (CompatDataPath != null ? System.IO.Path.Combine(CompatDataPath, "pfx") : null);
                if (prefix == null) return null;
                string steamPath = System.IO.Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Warframe", "EE.log");
                if (System.IO.File.Exists(steamPath))
                    return steamPath;
                string realPath = System.IO.Path.Combine(prefix, "drive_c", "users", System.Environment.UserName, "AppData", "Local", "Warframe", "EE.log");
                if (System.IO.File.Exists(realPath))
                    return realPath;
                return steamPath;
            }
        }
    }
}
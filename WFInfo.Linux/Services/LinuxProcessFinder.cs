using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Finds the Warframe process on Linux by scanning /proc.
    /// Under Proton, the process tree is:
    ///   steam -> pressure-vessel -> wine64-preloader -> Warframe.x64.exe
    /// </summary>
    public class LinuxProcessFinder : IProcessFinder, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Timer _pollTimer;
        private readonly object _lock = new();
        private Process _warframe;
        private int _lastPid;
        private WineEnvironmentInfo _wineEnv;

        public Process Warframe
        {
            get
            {
                CheckProcess();
                lock (_lock) { return _warframe; }
            }
        }

        public bool IsRunning
        {
            get
            {
                CheckProcess();
                lock (_lock)
                {
                    try { return _warframe != null && !_warframe.HasExited; }
                    catch { return false; }
                }
            }
        }

        public WineEnvironmentInfo WineEnvironment => _wineEnv;

        public event ProcessChangedArgs OnProcessChanged;

        public LinuxProcessFinder(ILogger logger)
        {
            _logger = logger;
            _pollTimer = new Timer(_ => PollProcess(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        private void PollProcess()
        {
            CheckProcess();

            Process snapshot;
            int currentPid;
            lock (_lock)
            {
                snapshot = _warframe;
                currentPid = snapshot?.Id ?? 0;
            }

            if (currentPid != _lastPid)
            {
                _lastPid = currentPid;
                OnProcessChanged?.Invoke(snapshot);
            }
        }

        private void CheckProcess()
        {
            lock (_lock)
            {
                if (_warframe != null)
                {
                    try { if (!_warframe.HasExited) return; }
                    catch { }
                    _warframe.Dispose();
                    _warframe = null;
                }

                _warframe = FindWarframeProcess();
                if (_warframe != null)
                {
                    _wineEnv = ReadWineEnvironment(_warframe.Id);
                    _logger.AddLog($"LinuxProcessFinder: Found Warframe PID={_warframe.Id}");
                    if (_wineEnv != null)
                        _logger.AddLog($"LinuxProcessFinder: Wine env, prefix={_wineEnv.WinePrefix}, EE.log={_wineEnv.EELogPath}");
                }
                else
                {
                    _wineEnv = null;
                }
            }
        }

        private Process FindWarframeProcess()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories("/proc"))
                {
                    string pidStr = Path.GetFileName(dir);
                    if (!int.TryParse(pidStr, out int pid))
                        continue;

                    try
                    {
                        string cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                        // Match the game executable, not the launcher (Launcher.exe also has "Warframe" in its path)
                        if (cmdline.Contains("Warframe.x64.exe", StringComparison.OrdinalIgnoreCase)
                            && !cmdline.Contains("Launcher.exe", StringComparison.OrdinalIgnoreCase))
                            return Process.GetProcessById(pid);
                    }
                    catch { }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxProcessFinder error: {ex.Message}");
                return null;
            }
        }

        private WineEnvironmentInfo ReadWineEnvironment(int pid)
        {
            try
            {
                string environPath = $"/proc/{pid}/environ";
                if (!File.Exists(environPath))
                    return null;

                byte[] raw = File.ReadAllBytes(environPath);
                string content = System.Text.Encoding.UTF8.GetString(raw);
                string[] entries = content.Split('\0');

                var env = new WineEnvironmentInfo();

                foreach (string entry in entries)
                {
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = entry.Substring(0, eq);
                    string val = entry.Substring(eq + 1);

                    switch (key)
                    {
                        case "WINEPREFIX": env.WinePrefix = val; break;
                        case "STEAM_COMPAT_DATA_PATH": env.CompatDataPath = val; break;
                        case "WINELOADER": env.WineLoaderPath = val; break;
                    }
                }

                if (env.WinePrefix != null || env.CompatDataPath != null)
                    return env;
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxProcessFinder: Failed to read Wine env: {ex.Message}");
            }
            return null;
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
            lock (_lock)
            {
                _warframe?.Dispose();
                _warframe = null;
            }
        }
    }
}
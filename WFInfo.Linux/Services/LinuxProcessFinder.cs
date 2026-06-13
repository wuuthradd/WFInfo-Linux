using System;
using System.Diagnostics;
using System.IO;


using System.Threading;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;
using static WFInfo.Linux.Services.X11Interop;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Finds the Warframe process on Linux by scanning /proc.
    /// Under Proton, the process tree is:
    ///   steam → pressure-vessel → wine64-preloader → Warframe.x64.exe
    /// </summary>
    public class LinuxProcessFinder : IProcessFinder, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Timer _pollTimer;
        private readonly object _lock = new();
        private Process _warframe;
        private long _windowId;
        private int _lastPid;
        private WineEnvironmentInfo _wineEnv;

        public Process Warframe
        {
            get
            {
                CheckProcess();
                return _warframe;
            }
        }

        public long WindowId => _windowId;

        public bool IsRunning
        {
            get
            {
                CheckProcess();
                return _warframe != null && !_warframe.HasExited;
            }
        }

        public bool GameIsStreamed => false;

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
            int currentPid = _warframe?.Id ?? 0;

            if (currentPid != _lastPid)
            {
                _lastPid = currentPid;
                OnProcessChanged?.Invoke(_warframe);
            }
        }

        private void CheckProcess()
        {
            lock (_lock)
            {
                if (_warframe != null)
                {
                    try
                    {
                        if (!_warframe.HasExited)
                        {
                            // Retry XID lookup if the window wasn't ready when the process was first found
                            if (_windowId == 0)
                            {
                                long xid = FindX11Window(_warframe.Id);
                                if (xid > 0)
                                {
                                    _windowId = xid;
                                    _logger.AddLog($"LinuxProcessFinder: Late window discovery XID=0x{xid:X}");
                                }
                            }
                            return;
                        }
                    }
                    catch { }
                    _warframe?.Dispose();
                    _warframe = null;
                    _windowId = 0;
                }

                _warframe = FindWarframeProcess();
                if (_warframe != null)
                {
                    _windowId = FindX11Window(_warframe.Id);
                    _wineEnv = ReadWineEnvironment(_warframe.Id);
                    _logger.AddLog($"LinuxProcessFinder: Found Warframe PID={_warframe.Id}, XID={_windowId}");
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
                        if (cmdline.Contains("Warframe.x64", StringComparison.OrdinalIgnoreCase))
                        {
                            return Process.GetProcessById(pid);
                        }
                    }
                    catch { }
                }

                // Fallback: check process names
                Process wf = null;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (wf == null && p.ProcessName.Contains("Warframe", StringComparison.OrdinalIgnoreCase))
                            wf = p;
                        else
                            p.Dispose();
                    }
                    catch { p.Dispose(); }
                }
                return wf;
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxProcessFinder error: {ex.Message}");
                return null;
            }
        }

        private long FindX11Window(int pid)
        {
            try
            {
                IntPtr display = SharedDisplay;
                if (display == IntPtr.Zero)
                    return 0;

                IntPtr root = XDefaultRootWindow(display);
                if (root == IntPtr.Zero)
                    return 0;

                IntPtr found = X11Interop.FindWindowByName(display, root, "Warframe");
                if (found != IntPtr.Zero)
                {
                    _logger.AddLog($"LinuxProcessFinder: Found window via X11 tree search: XID=0x{found.ToInt64():X}");
                    return found.ToInt64();
                }
            }
            catch (DllNotFoundException)
            {
                _logger.AddLog("LinuxProcessFinder: libX11.so.6 not found");
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxProcessFinder: X11 window search error: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Gets window geometry via X11 P/Invoke. Returns true if successful.
        /// Replaces xdotool getwindowgeometry.
        /// </summary>
        public bool GetWindowGeometry(long xid, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            if (xid <= 0) return false;

            try
            {
                IntPtr display = SharedDisplay;
                if (display == IntPtr.Zero) return false;

                IntPtr window = new IntPtr(xid);
                if (XGetWindowAttributes(display, window, out var attrs) != 0
                    && attrs.width > 0 && attrs.height > 0)
                {
                    width = attrs.width;
                    height = attrs.height;

                    IntPtr rootWindow = XDefaultRootWindow(display);
                    if (rootWindow != IntPtr.Zero &&
                        XTranslateCoordinates(display, window, rootWindow, 0, 0,
                            out int absX, out int absY, out _) != 0)
                    {
                        x = absX;
                        y = absY;
                    }
                    else
                    {
                        x = attrs.x;
                        y = attrs.y;
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        private WineEnvironmentInfo ReadWineEnvironment(int pid)
        {
            try
            {
                string environPath = $"/proc/{pid}/environ";
                if (!File.Exists(environPath))
                    return null;

                // /proc/<pid>/environ is null-byte separated KEY=VALUE pairs
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

                // Only return if we got at least one useful value
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
            _warframe?.Dispose();
            _warframe = null;
        }
    }
}
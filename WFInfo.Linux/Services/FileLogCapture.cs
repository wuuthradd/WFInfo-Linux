using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;

namespace WFInfo.Linux.Services
{
    public class FileLogCapture : ILogCapture
    {
        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenSource _captureCts;
        private string _logPath;
        private readonly ILogger _logger;
        private readonly IProcessFinder _processFinder;
        private Process _dbmonProcess;
        private ManualResetEventSlim _cursorEvent;
        private PixelPoint? _cursorResult;

        public event LogWatcherEventHandler TextChanged;

        public FileLogCapture(ILogger logger, IProcessFinder processFinder = null, string logPath = null)
        {
            _logger = logger;
            _processFinder = processFinder;
            string envPrefix = processFinder?.WineEnvironment?.WinePrefix;
            _logPath = logPath ?? PlatformPaths.FindEELogPath(envPrefix);

            if (_processFinder != null)
            {
                _processFinder.OnProcessChanged += OnGameProcessChanged;
                if (_processFinder.IsRunning)
                    StartNewCaptureTask();
            }
            else
            {
                StartNewCaptureTask();
            }
        }

        private void OnGameProcessChanged(Process process)
        {
            if (process == null || process.HasExited)
            {
                _logger.AddLog("FileLogCapture: Warframe exited, stopping capture");
                _captureCts?.Cancel();
                KillDbMon();
            }
            else
            {
                _logger.AddLog($"FileLogCapture: New game detected (PID={process.Id}), restarting capture");
                _captureCts?.Cancel();
                KillDbMon();
                string envPrefix = _processFinder?.WineEnvironment?.WinePrefix;
                _logPath = PlatformPaths.FindEELogPath(envPrefix);
                StartNewCaptureTask();
            }
        }

        private void StartNewCaptureTask()
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var old = Interlocked.Exchange(ref _captureCts, cts);
            old?.Cancel();
            old?.Dispose();
            var token = cts.Token;
            Task.Factory.StartNew(() => CaptureLoop(token), TaskCreationOptions.LongRunning).Unwrap();
        }

        private async Task CaptureLoop(CancellationToken token)
        {
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { return; }

            const int maxRetries = 5;
            int retryCount = 0;

            while (!token.IsCancellationRequested)
            {
                var result = await TryRunDbMon(token);
                if (token.IsCancellationRequested) return;

                switch (result)
                {
                    case DbMonResult.Unavailable:
                        _logger.AddLog("FileLogCapture: DBMON unavailable (missing binary or Wine), auto mode disabled");
                        return;

                    case DbMonResult.Crashed:
                        retryCount++;
                        if (retryCount > maxRetries)
                        {
                            _logger.AddLog($"FileLogCapture: DBMON failed {maxRetries} times, auto mode disabled");
                            return;
                        }
                        int delay = Math.Min(retryCount * 2000, 10_000);
                        _logger.AddLog($"FileLogCapture: DBMON crashed, retry {retryCount}/{maxRetries} in {delay / 1000}s");
                        try { await Task.Delay(delay, token); }
                        catch (OperationCanceledException) { return; }
                        break;

                    case DbMonResult.GameExited:
                        return;
                }
            }
        }

        private enum DbMonResult { Unavailable, Crashed, GameExited }

        #region DBMON bridge (OutputDebugString capture)

        private async Task<DbMonResult> TryRunDbMon(CancellationToken token)
        {
            try
            {
                if (_processFinder != null && !_processFinder.IsRunning)
                {
                    try { await Task.Delay(3000, token); }
                    catch (OperationCanceledException) { return DbMonResult.GameExited; }
                    if (!_processFinder.IsRunning)
                        return DbMonResult.GameExited;
                }

                string dbmonExe = FindDbMonExe();
                if (dbmonExe == null) return DbMonResult.Unavailable;

                string wineBin = FindWineBinary();
                if (wineBin == null) return DbMonResult.Unavailable;

                string prefix = FindWinePrefix();
                if (prefix == null) return DbMonResult.Unavailable;

                _logger.AddLog($"FileLogCapture: Starting DBMON bridge, wine={wineBin}, prefix={prefix}");

                var psi = new ProcessStartInfo
                {
                    FileName = wineBin,
                    Arguments = $"\"{dbmonExe}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                psi.Environment["WINEPREFIX"] = prefix;
                psi.Environment["WINEDEBUG"] = "-all";

                if (token.IsCancellationRequested) return DbMonResult.GameExited;

                _dbmonProcess = Process.Start(psi);
                if (_dbmonProcess == null)
                    return DbMonResult.Crashed;

                bool ready = false;
                var readyTask = Task.Run(() =>
                {
                    try
                    {
                        string line;
                        while ((line = _dbmonProcess.StandardError.ReadLine()) != null)
                        {
                            if (line.Contains("DBMON_READY"))
                            {
                                ready = true;
                                return;
                            }
                            if (line.Contains("DBMON_ERROR"))
                            {
                                _logger.AddLog($"FileLogCapture: DBMON error: {line}");
                                return;
                            }
                        }
                    }
                    catch { }
                });

                if (await Task.WhenAny(readyTask, Task.Delay(10_000, token)) != readyTask || !ready)
                {
                    KillDbMon();
                    if (token.IsCancellationRequested) return DbMonResult.GameExited;
                    _logger.AddLog("FileLogCapture: DBMON did not become ready in time");
                    return DbMonResult.Crashed;
                }

                _logger.AddLog("FileLogCapture: DBMON bridge active, instant OutputDebugString capture");

                await ReadDbMonOutput(token);

                if (token.IsCancellationRequested) return DbMonResult.GameExited;

                if (_processFinder != null && _processFinder.IsRunning)
                    return DbMonResult.Crashed;

                return DbMonResult.GameExited;
            }
            catch (OperationCanceledException) { return DbMonResult.GameExited; }
            catch (Exception ex)
            {
                _logger.AddLog($"FileLogCapture: DBMON failed: {ex.Message}");
                KillDbMon();
                return DbMonResult.Crashed;
            }
        }

        private async Task ReadDbMonOutput(CancellationToken token)
        {
            try
            {
                var reader = _dbmonProcess.StandardOutput;
                while (!token.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    if (line.Length > 0)
                    {
                        if (line.StartsWith("CURSOR "))
                        {
                            ParseCursorResponse(line);
                            continue;
                        }
                        if (line.Contains("Got rewards") || line.Contains("Pause countdown done"))
                            _logger.AddLog("DBMON: trigger line detected");
                        TextChanged?.Invoke(this, line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.AddLog($"FileLogCapture: DBMON read error: {ex.Message}");
            }
        }

        private void ParseCursorResponse(string line)
        {
            // Format: "CURSOR x y"
            var parts = line.Split(' ');
            if (parts.Length >= 3
                && int.TryParse(parts[1], out int x)
                && int.TryParse(parts[2], out int y))
            {
                _cursorResult = new PixelPoint(x, y);
            }
            else
            {
                _cursorResult = null;
            }
            try { _cursorEvent?.Set(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Query cursor position via DBMON's GetCursorPos.
        /// Returns null if DBMON is not running or the query times out.
        /// </summary>
        public PixelPoint? QueryCursorPosition()
        {
            if (_dbmonProcess == null || _dbmonProcess.HasExited)
                return null;

            _cursorResult = null;
            _cursorEvent = new ManualResetEventSlim(false);
            try
            {
                _dbmonProcess.StandardInput.WriteLine("CURSOR");
                _dbmonProcess.StandardInput.Flush();
                if (!_cursorEvent.Wait(200))
                {
                    _logger.AddLog("FileLogCapture: DBMON cursor query timeout");
                    return null;
                }
                return _cursorResult;
            }
            catch (Exception ex)
            {
                _logger.AddLog($"FileLogCapture: DBMON cursor query failed: {ex.Message}");
                return null;
            }
            finally
            {
                _cursorEvent.Dispose();
                _cursorEvent = null;
            }
        }

        private string FindWinePrefix()
        {
            string prefix = _processFinder?.WineEnvironment?.WinePrefix;
            if (prefix != null) return prefix;

            if (_processFinder?.WineEnvironment?.CompatDataPath != null)
                return Path.Combine(_processFinder.WineEnvironment.CompatDataPath, "pfx");

            if (_logPath != null)
            {
                int pfxIdx = _logPath.IndexOf(Path.Combine("pfx", "drive_c"), StringComparison.Ordinal);
                if (pfxIdx > 0)
                    return _logPath.Substring(0, pfxIdx + 3); // keep up to end of "pfx"
            }

            return null;
        }

        private string FindDbMonExe()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate = Path.Combine(appDir, "WFInfo.DbMon.exe");
            if (File.Exists(candidate)) return candidate;

            // Check AppImage usr/bin
            candidate = Path.Combine(appDir, "..", "bin", "WFInfo.DbMon.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

            // Check APPDIR env (set by AppImage runtime)
            string appImage = Environment.GetEnvironmentVariable("APPDIR");
            if (appImage != null)
            {
                candidate = Path.Combine(appImage, "usr", "bin", "WFInfo.DbMon.exe");
                if (File.Exists(candidate)) return candidate;
            }

            candidate = Path.Combine(appDir, "..", "..", "..", "DBMon", "WFInfo.DbMon.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

            _logger.AddLog("FileLogCapture: WFInfo.DbMon.exe not found");
            return null;
        }

        private string FindWineBinary()
        {
            // 1. WINELOADER from the game process environ (most reliable)
            string loader = _processFinder?.WineEnvironment?.WineLoaderPath;
            if (loader != null && File.Exists(loader))
                return loader;

            // 2. Derive from compat data config_info, or infer from EE.log path
            string compatData = _processFinder?.WineEnvironment?.CompatDataPath;
            if (compatData == null && _logPath != null)
            {
                int pfxIdx = _logPath.IndexOf(Path.Combine("pfx", "drive_c"), StringComparison.Ordinal);
                if (pfxIdx > 0)
                    compatData = _logPath.Substring(0, pfxIdx - 1); // strip separator before "pfx"
            }
            if (compatData != null)
            {
                string configInfo = Path.Combine(compatData, "config_info");
                if (File.Exists(configInfo))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(configInfo);
                        // Extract Proton base path from font paths in config_info
                        foreach (string cfgLine in lines)
                        {
                            int idx = cfgLine.IndexOf("/files/");
                            if (idx > 0)
                            {
                                string protonFiles = cfgLine.Substring(0, idx + 6); // include "/files"
                                string wine = Path.Combine(protonFiles, "bin", "wine");
                                if (File.Exists(wine)) return wine;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 3. System wine
            string systemWine = "/usr/bin/wine";
            if (File.Exists(systemWine))
                return systemWine;

            return null;
        }

        private void KillDbMon()
        {
            try
            {
                if (_dbmonProcess != null && !_dbmonProcess.HasExited)
                    _dbmonProcess.Kill(true);
            }
            catch { }
            _dbmonProcess?.Dispose();
            _dbmonProcess = null;
        }

        #endregion

        public void Dispose()
        {
            if (_processFinder != null)
                _processFinder.OnProcessChanged -= OnGameProcessChanged;
            _cts.Cancel();
            KillDbMon();
            _captureCts?.Cancel();
            _captureCts?.Dispose();
            _cts.Dispose();
        }
    }
}
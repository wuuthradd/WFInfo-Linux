using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using WFInfo.Models;
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
        private ManualResetEventSlim _focusEvent;
        private bool? _focusResult;
        private readonly object _queryLock = new object();

        // Trade detection state machine
        private enum TradeState { Idle, ParsingItems, WaitingConfirm }
        private TradeState _tradeState = TradeState.Idle;
        private List<TradeItem> _tradeTxItems = new();
        private List<TradeItem> _tradeRxItems = new();
        private string _tradePartner;
        private DateTime _tradeTimestamp;
        private bool _tradeParsingRx; // false=parsing TX (giving), true=parsing RX (receiving)

        private const string TradeDetectLine = "Are you sure you want to accept this trade? You are offering";
        private const string WillReceivePart1 = "and will receive from ";
        private const string WillReceivePart2 = " the following:";
        private const string TradeSuccessLine = "The trade was successful!";

        /// <summary>
        /// Fired when a new whisper/conversation tab is detected in game chat.
        /// The string argument is the player name.
        /// </summary>
        public event Action<string> OnWhisperDetected;

        /// <summary>
        /// Fired when a trade is successfully completed.
        /// </summary>
        public event Action<TradeInfo> OnTradeCompleted;

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
                        if (line.StartsWith("FOCUS "))
                        {
                            ParseFocusResponse(line);
                            continue;
                        }
                        if (line.Contains("Got rewards"))
                            _logger.AddLog("DBMON: trigger line detected");

                        if (line.Contains("ChatRedux::AddTab: Adding tab with channel name"))
                            HandleWhisperLine(line);

                        ProcessTradeLine(line);

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

        private void ParseFocusResponse(string line)
        {
            _focusResult = line.Length >= 7 && line[6] == '1';
            try { _focusEvent?.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void HandleWhisperLine(string line)
        {
            try
            {
                int idx = line.IndexOf("channel name: ");
                if (idx < 0) return;
                string name = line.Substring(idx + 14);
                int toIdx = name.IndexOf(" to index");
                if (toIdx < 0) return;
                name = name.Substring(0, toIdx);
                if (!name.StartsWith("F")) return;

                if (name.Length > 0 && IsPlatformSymbol(name[name.Length - 1]))
                    name = name.Substring(0, name.Length - 1);

                string playerName = name.Substring(1);
                if (string.IsNullOrEmpty(playerName)) return;

                Task.Run(() => OnWhisperDetected?.Invoke(playerName));
            }
            catch (Exception ex)
            {
                _logger.AddLog($"FileLogCapture: Failed to parse whisper line: {ex.Message}");
            }
        }

        private void ProcessTradeLine(string line)
        {
            if (line.Contains(TradeDetectLine))
            {
                _tradeTxItems.Clear();
                _tradeRxItems.Clear();
                _tradePartner = null;
                _tradeParsingRx = false;
                _tradeTimestamp = DateTime.UtcNow;
                _tradeState = TradeState.ParsingItems;
                _logger.AddLog("Trade: detected trade confirmation line");

                ParseTradeItemsFromLine(line);
                return;
            }

            if (_tradeState == TradeState.ParsingItems)
            {
                if (line.Contains("[Info]") || line.Contains("[Error]") || line.Contains("[Warning]"))
                {
                    _tradeState = TradeState.WaitingConfirm;
                    if (line.Contains(TradeSuccessLine))
                    {
                        FinalizeTrade();
                        return;
                    }
                    return;
                }
                ParseTradeItemsFromLine(line);
                return;
            }

            if (_tradeState == TradeState.WaitingConfirm)
            {
                if (line.Contains(TradeSuccessLine))
                {
                    FinalizeTrade();
                    return;
                }
                if ((DateTime.UtcNow - _tradeTimestamp).TotalMinutes > 15)
                    _tradeState = TradeState.Idle;
            }
        }

        private void ParseTradeItemsFromLine(string line)
        {
            if (line.Contains(WillReceivePart1) && line.Contains(WillReceivePart2))
            {
                int start = line.IndexOf(WillReceivePart1) + WillReceivePart1.Length;
                int end = line.IndexOf(WillReceivePart2, start);
                if (end > start)
                {
                    string name = line.Substring(start, end - start).Trim();
                    if (name.Length > 0 && IsPlatformSymbol(name[name.Length - 1]))
                        name = name.Substring(0, name.Length - 1);
                    _tradePartner = name;
                }
                _tradeParsingRx = true;
                return;
            }

            if (line.Contains(TradeDetectLine) || string.IsNullOrWhiteSpace(line))
                return;

            string text = line;

            int titleIdx = text.IndexOf(", title= leftItem=/");
            if (titleIdx >= 0)
                text = text.Substring(0, titleIdx);

            text = text.Replace("\r", "").Replace("\n", "").Trim();
            if (string.IsNullOrEmpty(text)) return;

            string itemName;
            int count = 1;
            int xIdx = text.IndexOf(" x ");
            if (xIdx >= 0)
            {
                itemName = text.Substring(0, xIdx).Trim();
                int.TryParse(text.Substring(xIdx + 3).Trim(), out count);
                if (count < 1) count = 1;
            }
            else
            {
                itemName = text;
            }

            int? rank = null;
            int filledPips = 0;
            int emptyPips = 0;
            foreach (char c in itemName)
            {
                if (c == '\uE0FC' || c == '\uE0B6') filledPips++;
                else if (c == '\uE0FF') emptyPips++;
            }
            if (filledPips + emptyPips > 0)
                rank = filledPips;

            int end2 = itemName.Length;
            while (end2 > 0 && (itemName[end2 - 1] > 127 || itemName[end2 - 1] == '\\' || itemName[end2 - 1] == ' '))
                end2--;
            itemName = end2 > 0 ? itemName.Substring(0, end2) : itemName;

            if (string.IsNullOrEmpty(itemName)) return;

            var targetList = _tradeParsingRx ? _tradeRxItems : _tradeTxItems;
            targetList.Add(new TradeItem(itemName, count, rank));
            string side = _tradeParsingRx ? "RX" : "TX";
            _logger.AddLog($"Trade: parsed {side} item: \"{itemName}\" x{count}" + (rank.HasValue ? $" rank={rank.Value}" : ""));
        }

        private void FinalizeTrade()
        {
            _tradeState = TradeState.Idle;

            if (_tradeTxItems.Count == 0 && _tradeRxItems.Count == 0)
                return;

            var trade = new TradeInfo
            {
                Given = new List<TradeItem>(_tradeTxItems),
                Received = new List<TradeItem>(_tradeRxItems),
                Partner = _tradePartner ?? "Unknown",
                Timestamp = _tradeTimestamp
            };

            _logger.AddLog($"Trade: finalized -- gave {trade.Given.Count} items, received {trade.Received.Count} items, isSale={trade.IsSale}");

            _tradeTxItems.Clear();
            _tradeRxItems.Clear();

            Task.Run(() => OnTradeCompleted?.Invoke(trade));
        }

        /// <summary>
        /// Query whether Warframe is the foreground window via DBMON's
        /// GetForegroundWindow + GetWindowThreadProcessId (Wine-translated).
        /// Returns null if DBMON is not running or the query times out.
        /// </summary>
        public bool? QueryFocusState()
        {
            if (_dbmonProcess == null || _dbmonProcess.HasExited)
                return null;

            lock (_queryLock)
            {
                _focusResult = null;
                _focusEvent = new ManualResetEventSlim(false);
                try
                {
                    _dbmonProcess.StandardInput.WriteLine("FOCUS");
                    _dbmonProcess.StandardInput.Flush();
                    if (!_focusEvent.Wait(200))
                    {
                        _logger.AddLog("FileLogCapture: DBMON focus query timeout");
                        return null;
                    }
                    return _focusResult;
                }
                catch (Exception ex)
                {
                    _logger.AddLog($"FileLogCapture: DBMON focus query failed: {ex.Message}");
                    return null;
                }
                finally
                {
                    _focusEvent.Dispose();
                    _focusEvent = null;
                }
            }
        }

        /// <summary>
        /// Query cursor position via DBMON's GetCursorPos.
        /// Returns null if DBMON is not running or the query times out.
        /// </summary>
        public PixelPoint? QueryCursorPosition()
        {
            if (_dbmonProcess == null || _dbmonProcess.HasExited)
                return null;

            lock (_queryLock)
            {
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
        }

        private static bool IsPlatformSymbol(char c)
        {
            // Warframe appends a PUA char after player names in log output.
            return c >= '\uE000' && c <= '\uE004';
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
            var wineEnv = _processFinder?.WineEnvironment;
            int? pid = _processFinder?.Warframe?.Id;

            if (pid != null)
            {
                string wine = FindWineFromProcExe(pid.Value);
                if (wine != null)
                {
                    _logger.AddLog($"FileLogCapture: Wine binary from /proc/{pid}/exe: {wine}");
                    return wine;
                }
            }

            string loader = wineEnv?.WineLoaderPath;
            if (loader != null)
            {
                loader = StripContainerPrefix(loader);
                if (File.Exists(loader))
                {
                    _logger.AddLog($"FileLogCapture: Wine binary from WINELOADER: {loader}");
                    return loader;
                }
            }

            string compatToolPaths = wineEnv?.CompatToolPaths;
            if (compatToolPaths != null)
            {
                string protonDir = StripContainerPrefix(compatToolPaths.Split(':')[0]);
                string wine = FindWineInProtonDir(protonDir);
                if (wine != null)
                {
                    _logger.AddLog($"FileLogCapture: Wine binary from STEAM_COMPAT_TOOL_PATHS: {wine}");
                    return wine;
                }
            }

            string compatData = wineEnv?.CompatDataPath;
            if (compatData == null && _logPath != null)
            {
                int pfxIdx = _logPath.IndexOf(Path.Combine("pfx", "drive_c"), StringComparison.Ordinal);
                if (pfxIdx > 0)
                    compatData = _logPath.Substring(0, pfxIdx - 1);
            }
            if (compatData != null)
            {
                string configInfo = Path.Combine(compatData, "config_info");
                if (File.Exists(configInfo))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(configInfo);
                        foreach (string cfgLine in lines)
                        {
                            foreach (string marker in new[] { "/files/", "/dist/" })
                            {
                                int idx = cfgLine.IndexOf(marker);
                                if (idx > 0)
                                {
                                    string protonSubdir = cfgLine.Substring(0, idx + marker.Length - 1);
                                    protonSubdir = StripContainerPrefix(protonSubdir);
                                    string wine = Path.Combine(protonSubdir, "bin", "wine");
                                    if (File.Exists(wine))
                                    {
                                        _logger.AddLog($"FileLogCapture: Wine binary from config_info: {wine}");
                                        return wine;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            _logger.AddLog("FileLogCapture: Could not find Proton wine binary");
            return null;
        }

        private static string FindWineInProtonDir(string protonDir)
        {
            foreach (string subdir in new[] { "files", "dist", "" })
            {
                string binDir = subdir.Length > 0 ? Path.Combine(protonDir, subdir, "bin") : Path.Combine(protonDir, "bin");
                foreach (string name in new[] { "wine", "wine64" })
                {
                    string path = Path.Combine(binDir, name);
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        private string FindWineFromProcExe(int pid)
        {
            try
            {
                var link = File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: true);
                string target = link?.FullName;
                if (target == null) return null;
                target = StripContainerPrefix(target);

                string dir = Path.GetDirectoryName(target);
                for (int i = 0; i < 6 && dir != null && dir != "/"; i++)
                {
                    foreach (string name in new[] { "wine", "wine64" })
                    {
                        string binPath = Path.Combine(dir, "bin", name);
                        if (File.Exists(binPath)) return binPath;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Steam pressure-vessel maps the host filesystem under /run/host/ inside the container.
        /// Paths from the game process env vars and /proc/pid/exe use container paths that
        /// don't exist on the host. Strip the prefix so the path resolves on the host.
        /// </summary>
        private static string StripContainerPrefix(string path)
        {
            const string prefix = "/run/host";
            if (path.StartsWith(prefix) && !File.Exists(path) && !Directory.Exists(path))
                return path.Substring(prefix.Length);
            return path;
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
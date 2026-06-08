using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Manages the native overlay helper process.
    /// Spawns a small native helper (cairo + pango) that creates overlay surfaces
    /// above fullscreen games, Wayland layer-shell or X11 override-redirect.
    /// </summary>
    public class NativeOverlayService : IDisposable
    {
        private Process _process;
        private StreamWriter _stdin;
        private volatile bool _ready;
        private bool _disposed;
        private readonly ILogger _logger;
        private int _crashCount;
        private DateTime _firstCrashTime;
        private readonly string _helperPath;

        /// <summary>Result from a native SnapIt selection.</summary>
        public class SnapItResult
        {
            public bool Cancelled { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int SurfW { get; set; }
            public int SurfH { get; set; }
        }

        /// <summary>Fired when the native SnapIt overlay produces a result or is cancelled.</summary>
        public event Action<SnapItResult> OnSnapItResult;

        public bool IsAvailable { get; }

        public NativeOverlayService(ILogger logger)
        {
            _logger = logger;
            _helperPath = FindHelperBinary();
            IsAvailable = _helperPath != null;
            if (IsAvailable)
                _logger.AddLog($"NativeOverlayService: helper found at {_helperPath}");
            else
                _logger.AddLog("NativeOverlayService: not available (no helper binary found)");
        }

        /// <summary>
        /// Pre-start the helper process so it's ready when the first overlay is needed.
        /// Call this during app initialization to avoid startup delay on first show.
        /// </summary>
        public void Start()
        {
            if (IsAvailable)
                EnsureStarted();
        }

        public void ShowOverlay(int id, int x, int y, int w, int h,
                                string name, string plat, string ducats,
                                string owned, bool vaulted,
                                string volume = null, string setPlat = null,
                                bool mastered = false, bool warning = false,
                                bool snapit = false, string highlight = null,
                                double minEff = 1.0, double maxEff = 2.5,
                                int delay = 0,
                                bool hideInfo = false, bool highContrast = false,
                                string detected = null)
        {
            if (!IsAvailable) return;
            EnsureStarted();
            if (!_ready) return;

            string highlightField = highlight != null ? $",\"highlight\":{JsonEscape(highlight)}" : "";
            string volumeField = volume != null ? $",\"volume\":{JsonEscape(volume)}" : "";
            string setField = setPlat != null ? $",\"set_plat\":{JsonEscape(setPlat)}" : "";
            string detectedField = detected != null ? $",\"detected\":{JsonEscape(detected)}" : "";
            string json = $"{{\"cmd\":\"show\",\"id\":{id},\"x\":{x},\"y\":{y},\"w\":{w},\"h\":{h}," +
                $"\"name\":{JsonEscape(name)},\"plat\":{JsonEscape(plat)}," +
                $"\"ducats\":{JsonEscape(ducats)},\"owned\":{JsonEscape(owned)}," +
                $"\"vaulted\":{(vaulted ? "true" : "false")}," +
                $"\"mastered\":{(mastered ? "true" : "false")}," +
                $"\"warning\":{(warning ? "true" : "false")}," +
                $"\"snapit\":{(snapit ? "true" : "false")}" +
                $"{volumeField}{setField}{highlightField}{detectedField}" +
                $",\"min_eff\":{minEff.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"max_eff\":{maxEff.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $",\"delay\":{delay}" +
                $",\"hide_info\":{(hideInfo ? "true" : "false")}" +
                $",\"high_contrast\":{(highContrast ? "true" : "false")}}}";
            SendLine(json);
        }

        public void HighlightOverlay(int id, string type)
        {
            if (!_ready) return;
            SendLine($"{{\"cmd\":\"highlight\",\"id\":{id},\"type\":{JsonEscape(type)}}}");
        }

        public void HideAll()
        {
            if (!_ready) return;
            SendLine("{\"cmd\":\"hide_all\"}");
        }

        public void ShowRewardWindowPart(int idx, string name, string plat, string ducats,
            string owned, bool vaulted, bool mastered, bool warning,
            string volume, string setPlat, bool hideInfo, bool highContrast, string highlight = null)
        {
            if (!IsAvailable) return;
            EnsureStarted();
            if (!_ready) return;

            string volumeField = volume != null ? $",\"volume\":{JsonEscape(volume)}" : "";
            string setField = setPlat != null ? $",\"set_plat\":{JsonEscape(setPlat)}" : "";
            string highlightField = highlight != null ? $",\"highlight\":{JsonEscape(highlight)}" : "";
            string json = $"{{\"cmd\":\"rw_show\",\"idx\":{idx}," +
                $"\"name\":{JsonEscape(name)},\"plat\":{JsonEscape(plat)}," +
                $"\"ducats\":{JsonEscape(ducats)},\"owned\":{JsonEscape(owned)}," +
                $"\"vaulted\":{(vaulted ? "true" : "false")}," +
                $"\"mastered\":{(mastered ? "true" : "false")}," +
                $"\"warning\":{(warning ? "true" : "false")}" +
                $"{volumeField}{setField}{highlightField}" +
                $",\"hide_info\":{(hideInfo ? "true" : "false")}" +
                $",\"high_contrast\":{(highContrast ? "true" : "false")}}}";
            SendLine(json);
        }

        public void HighlightRewardWindowPart(int idx, string type)
        {
            if (!_ready) return;
            SendLine($"{{\"cmd\":\"rw_highlight\",\"idx\":{idx},\"type\":{JsonEscape(type)}}}");
        }

        public void CommitRewardWindow()
        {
            if (!_ready) return;
            SendLine("{\"cmd\":\"rw_done\"}");
        }

        public void HideRewardWindow()
        {
            if (!_ready) return;
            SendLine("{\"cmd\":\"rw_hide\"}");
        }

        private void EnsureStarted()
        {
            if (_process != null && !_process.HasExited)
                return;

            var now = DateTime.UtcNow;
            if (_crashCount > 0 && (now - _firstCrashTime).TotalSeconds > 60)
                _crashCount = 0;

            if (_crashCount >= 3)
            {
                _logger.AddLog("NativeOverlayService: too many crashes, not restarting");
                return;
            }

            if (_process != null)
            {
                _crashCount++;
                if (_crashCount == 1) _firstCrashTime = now;
                _logger.AddLog($"NativeOverlayService: helper crashed ({_crashCount}/3)");
            }

            _ready = false;
            _process?.Dispose();
            _process = null;
            _stdin?.Dispose();
            _stdin = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _helperPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Pass bundled font directory so overlay can register Roboto Condensed
                string fontDir = Path.Combine(AppContext.BaseDirectory, "Resources", "RobotoCondensed");
                if (!Directory.Exists(fontDir))
                    fontDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                        "..", "..", "..", "..", "WFInfo.Linux", "Resources", "RobotoCondensed"));
                if (Directory.Exists(fontDir))
                    psi.Environment["WFINFO_FONT_DIR"] = fontDir;

                _process = Process.Start(psi);
                if (_process == null)
                {
                    _logger.AddLog("NativeOverlayService: failed to start helper");
                    return;
                }
                _process.EnableRaisingEvents = true;

                _stdin = _process.StandardInput;
                _stdin.AutoFlush = true;

                // Wait for READY signal on stderr (up to 3 seconds)
                _process.ErrorDataReceived += OnStderrData;
                _process.BeginErrorReadLine();

                // Read stdout for events (snapit results)
                _process.OutputDataReceived += OnStdoutData;
                _process.BeginOutputReadLine();

                int waited = 0;
                while (!_ready && waited < 3000 && !_process.HasExited)
                {
                    Thread.Sleep(50);
                    waited += 50;
                }

                if (_ready)
                    _logger.AddLog("NativeOverlayService: helper ready (pid=" + _process.Id + ")");
                else
                    _logger.AddLog("NativeOverlayService: helper did not signal ready");
            }
            catch (Exception ex)
            {
                _logger.AddLog($"NativeOverlayService: start failed: {ex.Message}");
                _process = null;
            }
        }

        private void OnStderrData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            if (e.Data.Contains("READY"))
                _ready = true;
            else
                _logger.AddLog($"wfinfo-overlay: {e.Data}");
        }

        private void OnStdoutData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            try
            {
                if (e.Data.Contains("snapit_result"))
                {
                    // {"event":"snapit_result","x":X,"y":Y,"w":W,"h":H}
                    var result = new SnapItResult
                    {
                        Cancelled = false,
                        X = ParseJsonInt(e.Data, "\"x\":", 0),
                        Y = ParseJsonInt(e.Data, "\"y\":", 0),
                        Width = ParseJsonInt(e.Data, "\"w\":", 0),
                        Height = ParseJsonInt(e.Data, "\"h\":", 0),
                        SurfW = ParseJsonInt(e.Data, "\"sw\":", 0),
                        SurfH = ParseJsonInt(e.Data, "\"sh\":", 0)
                    };
                    _logger.AddLog($"NativeOverlayService: SnapIt result ({result.X},{result.Y}) {result.Width}x{result.Height}");
                    OnSnapItResult?.Invoke(result);
                }
                else if (e.Data.Contains("snapit_cancel"))
                {
                    _logger.AddLog("NativeOverlayService: SnapIt cancelled");
                    OnSnapItResult?.Invoke(new SnapItResult { Cancelled = true });
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"NativeOverlayService: stdout parse error: {ex.Message}");
            }
        }

        /// <summary>Parse a simple integer value from JSON like "key":123</summary>
        private static int ParseJsonInt(string json, string key, int def)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return def;
            idx += key.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-')) idx++;
            if (idx == start) return def;
            return int.TryParse(json.Substring(start, idx - start), out int val) ? val : def;
        }

        /// <summary>
        /// Start the native SnapIt overlay. The overlay covers the full screen
        /// above the game (layer-shell OVERLAY), accepts pointer input for selection.
        /// Keyboard cancel is handled by C# evdev → CancelSnapIt(). Result comes via OnSnapItResult event.
        /// </summary>
        public void StartSnapIt(int gameWidth, int gameHeight)
        {
            if (!IsAvailable) return;
            EnsureStarted();
            if (!_ready) return;

            string json = $"{{\"cmd\":\"snapit\",\"w\":{gameWidth},\"h\":{gameHeight}}}";
            SendLine(json);
        }

        /// <summary>Cancel an active native SnapIt overlay (called from evdev key handler).</summary>
        public void CancelSnapIt()
        {
            if (!_ready) return;
            SendLine("{\"cmd\":\"cancel_snapit\"}");
        }

        private void SendLine(string line)
        {
            try
            {
                _stdin?.WriteLine(line);
            }
            catch (Exception ex)
            {
                _logger.AddLog($"NativeOverlayService: send failed: {ex.Message}");
                _ready = false;
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private string FindHelperBinary()
        {
            string exeDir = AppContext.BaseDirectory;
            string path = Path.Combine(exeDir, "wfinfo-overlay");
            if (File.Exists(path)) return path;

            // Check ../lib relative to bin (AppImage layout: usr/bin/ -> usr/lib/)
            path = Path.Combine(exeDir, "..", "lib", "wfinfo-overlay");
            if (File.Exists(path)) return Path.GetFullPath(path);

            // Check NativeOverlay build directory (dev mode)
            string devPath = Path.Combine(exeDir, "..", "..", "..", "..",
                "WFInfo.Linux", "NativeOverlay", "wfinfo-overlay");
            if (File.Exists(devPath)) return Path.GetFullPath(devPath);

            return null;
        }


        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    SendLine("{\"cmd\":\"quit\"}");
                    _process.WaitForExit(1000);
                    if (!_process.HasExited)
                        _process.Kill();
                }
            }
            catch { }

            _stdin?.Dispose();
            _process?.Dispose();
        }
    }
}
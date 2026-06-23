using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using WFInfo;
using WFInfo.Services;
using WFInfo.Services.Screenshot;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// IPC client for the WFInfo Vulkan layer (libwfinfo_vk.so).
    /// Provides screenshot capture via IScreenshotService and overlay
    /// compositing. Connects to the layer's unix socket.
    /// </summary>
    public class VulkanLayerService : IScreenshotService, IDisposable
    {
        /// <summary>Result from a SnapIt selection in the Vulkan layer overlay.</summary>
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

        private static readonly string SocketPath =
            Path.Combine(PlatformPaths.AppDataPath, "wfinfo_layer.sock");

        private readonly ILogger _logger;
        private Socket _socket;
        private NetworkStream _stream;
        private readonly object _lock = new object();
        private bool _disposed;
        private Thread _readerThread;
        private volatile bool _readerRunning;
        private volatile bool _captureBusy;

        /// <summary>Fired when the Vulkan layer sends a snapit result or cancellation.</summary>
        public event Action<SnapItResult> OnSnapItResult;

        public bool IsAvailable => File.Exists(SocketPath);
        public bool IsConnected { get { var s = _socket; return s != null && s.Connected; } }

        /// <summary>True when the running layer's build doesn't match the installed .so.</summary>
        public bool IsStale { get; private set; }

        public VulkanLayerService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Force disconnect and reconnect to the layer socket.
        /// Use when the game has restarted and the old socket may be stale.
        /// </summary>
        public bool Reconnect()
        {
            lock (_lock)
            {
                Disconnect();
            }
            return Connect();
        }

        /// <summary>
        /// Attempt to connect to the layer socket. Returns true if connected.
        /// </summary>
        public bool Connect()
        {
            lock (_lock)
            {
                if (_socket != null && _socket.Connected)
                    return true;

                Disconnect();

                if (!File.Exists(SocketPath))
                    return false;

                try
                {
                    _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    _socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                    _socket.ReceiveTimeout = 5000;
                    _stream = new NetworkStream(_socket, ownsSocket: false);

                    _logger.AddLog("VulkanLayerService: connected to layer socket");
                    CheckStaleness();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.AddLog($"VulkanLayerService: connect failed: {ex.Message}");
                    Disconnect();
                    return false;
                }
            }
        }

        private void Disconnect()
        {
            StopEventPoller();
            try { _stream?.Dispose(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _stream = null;
            _socket = null;
        }

        private bool EnsureConnected()
        {
            if (IsStale)
                return false;
            if (_socket != null && _socket.Connected)
                return true;
            return Connect();
        }

        // ---- IScreenshotService ----

        public Task<List<SKBitmap>> CaptureScreenshot()
        {
            var bitmaps = new List<SKBitmap>();

            lock (_lock)
            {
                if (!EnsureConnected())
                {
                    if (IsStale)
                        AppMain.StatusUpdate("Vulkan layer outdated, restart Warframe to apply updates", 1);
                    return Task.FromResult(bitmaps);
                }

                _captureBusy = true;
                try
                {
                    SendLine("{\"cmd\":\"capture\"}");

                    // Read lines, processing any async events that arrive before the response
                    string headerLine;
                    while (true)
                    {
                        headerLine = ReadLine(_stream);
                        if (headerLine == null)
                        {
                            _logger.AddLog("VulkanLayerService: no response from layer");
                            Disconnect();
                            return Task.FromResult(bitmaps);
                        }
                        if (headerLine.Contains("snapit_result") || headerLine.Contains("snapit_cancel"))
                        {
                            HandleAsyncEvent(headerLine);
                            continue;
                        }
                        break;
                    }

                    var header = JObject.Parse(headerLine);

                    if (header["error"] != null)
                    {
                        _logger.AddLog($"VulkanLayerService: capture error: {header["error"]}");
                        return Task.FromResult(bitmaps);
                    }

                    int width = header.Value<int>("width");
                    int height = header.Value<int>("height");
                    int stride = header.Value<int>("stride");
                    long size = header.Value<long>("size");
                    string format = header.Value<string>("format") ?? "bgra8888";

                    if (width <= 0 || height <= 0 || stride <= 0 || size <= 0)
                    {
                        _logger.AddLog($"VulkanLayerService: invalid frame {width}x{height} stride={stride} size={size}");
                        Disconnect();
                        return Task.FromResult(bitmaps);
                    }

                    // Cap at 256 MB to guard against corrupt protocol data
                    if (size > 256 * 1024 * 1024)
                    {
                        _logger.AddLog($"VulkanLayerService: frame size {size} exceeds 256 MB cap, dropping");
                        Disconnect();
                        return Task.FromResult(bitmaps);
                    }

                    byte[] pixelData = ReadExact(_stream, (int)size);
                    if (pixelData == null)
                    {
                        _logger.AddLog("VulkanLayerService: failed to read pixel data");
                        Disconnect();
                        return Task.FromResult(bitmaps);
                    }

                    // Swap R and B if the swapchain is RGBA so all downstream
                    // code (theme detection, OCR filtering) sees BGRA byte order.
                    if (format == "rgba8888")
                    {
                        for (int i = 0; i < pixelData.Length - 3; i += 4)
                        {
                            byte tmp = pixelData[i];
                            pixelData[i] = pixelData[i + 2];
                            pixelData[i + 2] = tmp;
                        }
                    }

                    var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    var bitmap = new SKBitmap(info);

                    unsafe
                    {
                        fixed (byte* src = pixelData)
                        {
                            var dstPtr = bitmap.GetPixels();
                            if (stride == info.RowBytes)
                            {
                                Buffer.MemoryCopy(src, (void*)dstPtr,
                                    info.BytesSize, Math.Min(pixelData.Length, info.BytesSize));
                            }
                            else
                            {
                                int copyBytes = Math.Min(stride, info.RowBytes);
                                for (int y = 0; y < height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        src + y * stride,
                                        (byte*)dstPtr + y * info.RowBytes,
                                        info.RowBytes, copyBytes);
                                }
                            }
                        }
                    }

                    _logger.AddLog($"VulkanLayerService: captured {width}x{height}");
                    bitmaps.Add(bitmap);
                }
                catch (IOException ex) when (ex.InnerException is SocketException se &&
                    (se.SocketErrorCode == SocketError.TimedOut ||
                     se.SocketErrorCode == SocketError.ConnectionReset))
                {
                    _logger.AddLog($"VulkanLayerService: capture timeout/reset, reconnecting");
                    Disconnect();
                }
                catch (Exception ex)
                {
                    _logger.AddLog($"VulkanLayerService: capture failed: {ex.Message}");
                    Disconnect();
                }
                finally
                {
                    _captureBusy = false;
                }
            }

            return Task.FromResult(bitmaps);
        }

        // ---- Window info query ----

        /// <summary>
        /// Query swapchain dimensions from the Vulkan layer.
        /// Returns (width, height) or (0, 0) if unavailable.
        /// </summary>
        public (int width, int height) QueryWindowInfo()
        {
            lock (_lock)
            {
                if (!EnsureConnected())
                    return (0, 0);

                try
                {
                    SendLine("{\"cmd\":\"query_info\"}");

                    string line;
                    while (true)
                    {
                        line = ReadLine(_stream);
                        if (line == null) { Disconnect(); return (0, 0); }
                        if (line.Contains("snapit_result") || line.Contains("snapit_cancel"))
                        {
                            HandleAsyncEvent(line);
                            continue;
                        }
                        break;
                    }

                    int w = ParseJsonInt(line, "\"width\":", 0);
                    int h = ParseJsonInt(line, "\"height\":", 0);
                    return (w, h);
                }
                catch (Exception ex)
                {
                    _logger.AddLog($"VulkanLayerService: query_info failed: {ex.Message}");
                    Disconnect();
                    return (0, 0);
                }
            }
        }

        // ---- Overlay commands ----

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
            var sb = new StringBuilder(256);
            sb.Append("{\"cmd\":\"show\"");
            sb.Append(",\"id\":").Append(id);
            sb.Append(",\"x\":").Append(x);
            sb.Append(",\"y\":").Append(y);
            sb.Append(",\"w\":").Append(w);
            sb.Append(",\"h\":").Append(h);
            sb.Append(",\"name\":").Append(JsonEscape(name));
            sb.Append(",\"plat\":").Append(JsonEscape(plat));
            sb.Append(",\"ducats\":").Append(JsonEscape(ducats));
            sb.Append(",\"owned\":").Append(JsonEscape(owned));
            sb.Append(",\"vaulted\":").Append(vaulted ? "true" : "false");
            sb.Append(",\"mastered\":").Append(mastered ? "true" : "false");
            sb.Append(",\"warning\":").Append(warning ? "true" : "false");
            sb.Append(",\"snapit\":").Append(snapit ? "true" : "false");
            if (volume != null) sb.Append(",\"volume\":").Append(JsonEscape(volume));
            if (setPlat != null) sb.Append(",\"set_plat\":").Append(JsonEscape(setPlat));
            if (highlight != null) sb.Append(",\"highlight\":").Append(JsonEscape(highlight));
            if (detected != null) sb.Append(",\"detected\":").Append(JsonEscape(detected));
            sb.Append(",\"min_eff\":").Append(minEff.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"max_eff\":").Append(maxEff.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"delay\":").Append(delay);
            sb.Append(",\"hide_info\":").Append(hideInfo ? "true" : "false");
            sb.Append(",\"high_contrast\":").Append(highContrast ? "true" : "false");
            sb.Append('}');
            SendCommand(sb.ToString());
        }

        public void HighlightOverlay(int id, string type)
        {
            SendCommand($"{{\"cmd\":\"highlight\",\"id\":{id},\"type\":{JsonEscape(type)}}}");
        }

        public void HideAll()
        {
            SendCommand("{\"cmd\":\"hide_all\"}");
        }

        public void StartSnapIt(int gameWidth, int gameHeight)
        {
            SendCommand($"{{\"cmd\":\"snapit\",\"w\":{gameWidth},\"h\":{gameHeight}}}");
            StartEventPoller();
        }

        public void CancelSnapIt()
        {
            SendCommand("{\"cmd\":\"cancel_snapit\"}");
            StopEventPoller();
        }

        // ---- Async event polling for snapit results ----

        private void StartEventPoller()
        {
            StopEventPoller();
            _readerRunning = true;
            _readerThread = new Thread(EventPollerLoop)
            {
                Name = "VkLayerEventPoller",
                IsBackground = true
            };
            _readerThread.Start();
        }

        private void StopEventPoller()
        {
            _readerRunning = false;
            _readerThread?.Join(2000);
            _readerThread = null;
        }

        /// <summary>
        /// Polls the socket for async events (snapit_result, snapit_cancel).
        /// Runs only while snapit is active. Pauses during capture (held by _lock).
        /// </summary>
        private void EventPollerLoop()
        {
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (_readerRunning)
            {
                Thread.Sleep(50);
                if (_captureBusy) continue;
                lock (_lock)
                {
                    if (_socket == null || !_socket.Connected) continue;
                    try
                    {
                        if (!_socket.Poll(0, SelectMode.SelectRead)) continue;
                        int n = _socket.Receive(buf, 0, buf.Length, SocketFlags.None);
                        if (n <= 0) continue;

                        sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                        string all = sb.ToString();
                        int lastNl = all.LastIndexOf('\n');
                        if (lastNl < 0) continue;

                        string complete = all.Substring(0, lastNl);
                        sb.Clear();
                        if (lastNl + 1 < all.Length)
                            sb.Append(all.Substring(lastNl + 1));

                        foreach (string raw in complete.Split('\n'))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0) continue;
                            if (line.Contains("snapit_result") || line.Contains("snapit_cancel"))
                                HandleAsyncEvent(line);
                        }
                    }
                    catch { }
                }
            }
        }

        private void HandleAsyncEvent(string line)
        {
            try
            {
                if (line.Contains("snapit_result"))
                {
                    _readerRunning = false;
                    var result = new SnapItResult
                    {
                        Cancelled = false,
                        X = ParseJsonInt(line, "\"x\":", 0),
                        Y = ParseJsonInt(line, "\"y\":", 0),
                        Width = ParseJsonInt(line, "\"w\":", 0),
                        Height = ParseJsonInt(line, "\"h\":", 0),
                        SurfW = ParseJsonInt(line, "\"sw\":", 0),
                        SurfH = ParseJsonInt(line, "\"sh\":", 0)
                    };
                    _logger.AddLog($"VulkanLayerService: SnapIt result ({result.X},{result.Y}) {result.Width}x{result.Height}");
                    OnSnapItResult?.Invoke(result);
                }
                else if (line.Contains("snapit_cancel"))
                {
                    _readerRunning = false;
                    _logger.AddLog("VulkanLayerService: SnapIt cancelled");
                    OnSnapItResult?.Invoke(new SnapItResult { Cancelled = true });
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"VulkanLayerService: event parse error: {ex.Message}");
            }
        }

        // ---- build staleness check ----

        /// <summary>
        /// Queries the running layer's build ID and compares it to the
        /// installed .so on disk. Sets IsStale if they differ.
        /// Must be called inside _lock with a live connection.
        /// </summary>
        private void CheckStaleness()
        {
            try
            {
                SendLine("{\"cmd\":\"query_info\"}");
                string line;
                while (true)
                {
                    line = ReadLine(_stream);
                    if (line == null) return;
                    if (line.Contains("snapit_result") || line.Contains("snapit_cancel"))
                    {
                        HandleAsyncEvent(line);
                        continue;
                    }
                    break;
                }

                string runningBuild = ParseJsonString(line, "\"build\":\"");
                if (runningBuild == null)
                {
                    // Layer predates build ID support, skip check
                    _logger.AddLog("VulkanLayerService: layer has no build ID, skipping staleness check");
                    return;
                }

                string installedSo = Path.Combine(PlatformPaths.AppDataPath, "libwfinfo_vk.so");
                string installedBuild = ExtractBuildFromSo(installedSo);

                if (installedBuild != null && runningBuild != installedBuild)
                {
                    IsStale = true;
                    _logger.AddLog($"VulkanLayerService: layer is stale (running={runningBuild}, installed={installedBuild}). Restart Warframe to apply updates.");
                    AppMain.StatusUpdate("Vulkan layer outdated, restart Warframe to apply updates", 1);
                }
                else
                {
                    IsStale = false;
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"VulkanLayerService: staleness check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans the .so binary for the WFINFO_BUILD= marker and extracts
        /// the build timestamp that follows it.
        /// </summary>
        private static string ExtractBuildFromSo(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                byte[] marker = System.Text.Encoding.ASCII.GetBytes("WFINFO_BUILD=");
                for (int i = 0; i <= data.Length - marker.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < marker.Length; j++)
                    {
                        if (data[i + j] != marker[j]) { match = false; break; }
                    }
                    if (!match) continue;

                    int start = i + marker.Length;
                    int end = start;
                    while (end < data.Length && data[end] != 0 && end - start < 64)
                        end++;
                    return System.Text.Encoding.ASCII.GetString(data, start, end - start);
                }
            }
            catch { }
            return null;
        }

        private static string ParseJsonString(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        // ---- IPC helpers ----

        private void SendCommand(string json)
        {
            lock (_lock)
            {
                if (!EnsureConnected())
                {
                    if (IsStale)
                        AppMain.StatusUpdate("Vulkan layer outdated, restart Warframe to apply updates", 1);
                    return;
                }
                try
                {
                    SendLine(json);
                }
                catch (Exception)
                {
                    // Reconnect and retry once
                    Disconnect();
                    if (!Connect()) return;
                    try { SendLine(json); }
                    catch (Exception ex2)
                    {
                        _logger.AddLog($"VulkanLayerService: send failed: {ex2.Message}");
                        Disconnect();
                    }
                }
            }
        }

        private void SendLine(string line)
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
        }

        private static string ReadLine(Stream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\n') return sb.ToString();
                sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = stream.Read(buf, offset, count - offset);
                if (n <= 0) return null;
                offset += n;
            }
            return buf;
        }

        private static int ParseJsonInt(string json, string key, int def)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return def;
            idx += key.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-')) idx++;
            if (idx == start) return def;
            return int.TryParse(json.Substring(start, idx - start), out int val) ? val : def;
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Stop the poller before acquiring the lock so the join doesn't
            // deadlock against EventPollerLoop which also acquires _lock.
            _readerRunning = false;
            _readerThread?.Join(2000);
            _readerThread = null;

            lock (_lock)
            {
                try
                {
                    SendLine("{\"cmd\":\"quit\"}");
                }
                catch { }
                // Disconnect without calling StopEventPoller again (already stopped above)
                try { _stream?.Dispose(); } catch { }
                try { _socket?.Dispose(); } catch { }
                _stream = null;
                _socket = null;
            }
        }
    }
}
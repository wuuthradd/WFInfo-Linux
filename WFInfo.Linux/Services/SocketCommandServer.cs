using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    public class SocketCommandServer : IDisposable
    {
        private readonly string _socketPath;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private Socket _listener;

        public event Action<string> OnCommand;

        public SocketCommandServer(ILogger logger)
        {
            _logger = logger;
            string runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir))
            {
                runtimeDir = Path.Combine(Path.GetTempPath(), $"wfinfo-{Environment.UserName}");
                Directory.CreateDirectory(runtimeDir);
            }
            _socketPath = Path.Combine(runtimeDir, "wfinfo.sock");
        }

        public string SocketPath => _socketPath;

        public void Start()
        {
            try
            {
                if (File.Exists(_socketPath))
                    File.Delete(_socketPath);

                var endpoint = new UnixDomainSocketEndPoint(_socketPath);
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(endpoint);
                _listener.Listen(5);

                try
                {
#pragma warning disable CA1416
                    File.SetUnixFileMode(_socketPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
                }
                catch { }

                Task.Factory.StartNew(AcceptLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _logger.AddLog($"SocketCommandServer: listening on {_socketPath}");
            }
            catch (Exception ex)
            {
                _logger.AddLog($"SocketCommandServer: failed to start: {ex.Message}");
            }
        }

        private void AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket client = null;
                try
                {
                    client = _listener.Accept();
                    byte[] buffer = new byte[256];
                    int received = client.Receive(buffer);
                    if (received > 0)
                    {
                        string command = Encoding.UTF8.GetString(buffer, 0, received).Trim();
                        if (command.Length > 0)
                        {
                            _logger.AddLog($"SocketCommandServer: received '{command}'");
                            OnCommand?.Invoke(command.ToLowerInvariant());
                        }
                    }
                }
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_cts.IsCancellationRequested)
                        _logger.AddLog($"SocketCommandServer: error handling connection: {ex.Message}");
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Close(); } catch { }
            try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
            _cts.Dispose();
        }
    }
}
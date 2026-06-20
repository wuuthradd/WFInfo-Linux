using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WFInfo.Models;
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    public class LinuxInputListener : IInputListener
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<VirtualKey, bool> _heldKeys = new();
        private readonly ConcurrentDictionary<string, byte> _activeDevices = new();
        private FileSystemWatcher _watcher;

        public event EventHandler<KeyEventArgs> KeyEvent;
        public event EventHandler<MouseEventArgs> MouseEvent;
        public string StartupWarning { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct InputEvent
        {
            public long tvSec;
            public long tvUsec;
            public ushort type;
            public ushort code;
            public int value;
        }

        private const ushort EV_KEY = 0x01;

        public LinuxInputListener(ILogger logger)
        {
            _logger = logger;
            Task.Run(StartListenerChain);
        }

        private void StartListenerChain()
        {
            if (TryStartEvdev())
            {
                StartHotplugWatcher();
                return;
            }

            _logger.AddLog("LinuxInputListener: No permission for input devices. Global hotkeys unavailable.");
            _logger.AddLog("LinuxInputListener: Run:  sudo ./WFInfo --setup-input  (then log out and back in)");
            StartupWarning = "Global hotkeys unavailable - no input device access.\nRun:  sudo ./WFInfo --setup-input";
            StartHotplugWatcher();
        }

        private bool TryStartEvdev()
        {
            var kbdDevices = FindKeyboardDevices();
            var mouseDevices = FindMouseDevices();

            var allDevices = new HashSet<string>(kbdDevices);
            foreach (var d in mouseDevices) allDevices.Add(d);

            if (allDevices.Count == 0)
            {
                _logger.AddLog("LinuxInputListener: No keyboard/mouse device found in /dev/input");
                return false;
            }

            bool permissionDenied = false;
            int kbdOpened = 0, mouseOpened = 0;
            foreach (var dev in allDevices)
            {
                if (_activeDevices.ContainsKey(dev)) continue;
                if (TryOpenDevice(dev))
                {
                    if (kbdDevices.Contains(dev)) kbdOpened++;
                    if (mouseDevices.Contains(dev)) mouseOpened++;
                }
                else if (IsPermissionError(dev))
                    permissionDenied = true;
            }

            int totalOpened = kbdOpened + mouseOpened;
            if (totalOpened == 0 && _activeDevices.IsEmpty)
            {
                if (permissionDenied)
                {
                    _logger.AddLog("LinuxInputListener: No permission for evdev (input devices).");
                    _logger.AddLog("LinuxInputListener: For reliable hotkeys, run:  sudo ./WFInfo --setup-input");
                }
                return false;
            }

            if (permissionDenied)
            {
                int kbdDenied = 0;
                foreach (var dev in kbdDevices)
                    if (!_activeDevices.ContainsKey(dev) && IsPermissionError(dev))
                        kbdDenied++;

                if (kbdDenied > 0)
                {
                    _logger.AddLog($"LinuxInputListener: Opened {totalOpened} device(s) but {kbdDenied} keyboard(s) denied, hotkeys may not work.");
                    _logger.AddLog("LinuxInputListener: Some devices opened via other groups (e.g. openrazer) but your main keyboard may not be among them.");
                    _logger.AddLog("LinuxInputListener: Run:  sudo ./WFInfo --setup-input  (then log out and back in)");
                    StartupWarning = "Global hotkeys may not work - some keyboard devices not accessible.\nRun:  sudo ./WFInfo --setup-input";
                }
            }

            return true;
        }

        private bool TryOpenDevice(string devicePath)
        {
            try
            {
                using (new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
            }
            catch { return false; }

            if (!_activeDevices.TryAdd(devicePath, 0)) return false;

            Task.Factory.StartNew(() => ReadEvdev(devicePath), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _logger.AddLog($"LinuxInputListener: Opened {devicePath}");
            return true;
        }

        private static bool IsPermissionError(string devicePath)
        {
            try
            {
                using (new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        private void StartHotplugWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher("/dev/input")
                {
                    Filter = "event*",
                    NotifyFilter = NotifyFilters.FileName
                };
                _watcher.Created += OnDeviceAdded;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxInputListener: Could not watch /dev/input for hotplug: {ex.Message}");
            }
        }

        private void OnDeviceAdded(object sender, FileSystemEventArgs e)
        {
            if (_cts.IsCancellationRequested) return;
            Task.Run(async () =>
            {
                try { await Task.Delay(500, _cts.Token); }
                catch (OperationCanceledException) { return; }
                string path = e.FullPath;
                if (_activeDevices.ContainsKey(path)) return;
                var kbdDevices = FindKeyboardDevices();
                var mouseDevices = FindMouseDevices();
                if (!kbdDevices.Contains(path) && !mouseDevices.Contains(path)) return;
                if (TryOpenDevice(path))
                    _logger.AddLog($"LinuxInputListener: Hotplug - attached {path}");
            });
        }

        /// <summary>
        /// Finds keyboard event devices. Requires 'kbd' in handlers,
        /// excludes devices that also have 'mouse' (peripheral sub-interfaces).
        /// Prefers devices with 'leds' (real keyboards have LED indicators).
        /// </summary>
        private List<string> FindKeyboardDevices()
        {
            var result = new List<string>();
            var kbdOnly = new List<string>();
            try
            {
                if (!File.Exists("/proc/bus/input/devices"))
                    return result;

                string content = File.ReadAllText("/proc/bus/input/devices");
                string[] blocks = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

                foreach (string block in blocks)
                {
                    string handlers = null;
                    foreach (string line in block.Split('\n'))
                    {
                        if (line.StartsWith("H: Handlers="))
                        {
                            handlers = line.Substring(12);
                            break;
                        }
                    }
                    if (handlers == null) continue;

                    if (!handlers.Contains("kbd")) continue;
                    if (handlers.Contains("mouse")) continue;

                    bool hasLeds = handlers.Contains("leds");

                    foreach (string handler in handlers.Split(' '))
                    {
                        if (handler.StartsWith("event"))
                        {
                            string path = $"/dev/input/{handler}";
                            if (File.Exists(path))
                            {
                                if (hasLeds)
                                    result.Add(path);
                                else
                                    kbdOnly.Add(path);
                            }
                        }
                    }
                }
            }
            catch { }
            return result.Count > 0 ? result : kbdOnly;
        }

        private List<string> FindMouseDevices()
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists("/proc/bus/input/devices"))
                    return result;

                string content = File.ReadAllText("/proc/bus/input/devices");
                string[] blocks = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

                foreach (string block in blocks)
                {
                    string handlers = null;
                    foreach (string line in block.Split('\n'))
                    {
                        if (line.StartsWith("H: Handlers="))
                        {
                            handlers = line.Substring(12);
                            break;
                        }
                    }
                    if (handlers == null) continue;
                    if (!handlers.Contains("mouse")) continue;

                    foreach (string handler in handlers.Split(' '))
                    {
                        if (handler.StartsWith("event"))
                        {
                            string path = $"/dev/input/{handler}";
                            if (File.Exists(path))
                                result.Add(path);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private void ReadEvdev(string devicePath)
        {
            try
            {
                int eventSize = Marshal.SizeOf<InputEvent>();
                byte[] buffer = new byte[eventSize];
                using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                while (!_cts.IsCancellationRequested)
                {
                    int bytesRead = fs.Read(buffer, 0, eventSize);
                    if (bytesRead == 0) break;
                    if (bytesRead < eventSize) continue;

                    var ev = MemoryMarshal.Read<InputEvent>(buffer);
                    if (ev.type == EV_KEY && ev.value != 2) // 0=release, 1=press, 2=repeat (skip repeats)
                    {
                        if (ev.code >= 272 && ev.code <= 276)
                        {
                            var btn = EvdevToMouseButton(ev.code);
                            if (btn.HasValue && ev.value == 1)
                                MouseEvent?.Invoke(this, new MouseEventArgs { Button = btn.Value });
                        }
                        else if (ev.code < 256)
                        {
                            VirtualKey key = EvdevToVirtualKey(ev.code);
                            if (key != VirtualKey.None)
                            {
                                bool isDown = ev.value == 1;
                                if (isDown) _heldKeys[key] = true; else _heldKeys.TryRemove(key, out _);
                                KeyEvent?.Invoke(this, new KeyEventArgs { Key = key, IsDown = isDown });
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxInputListener: evdev read error on {devicePath}: {ex.Message}");
            }
            finally
            {
                _activeDevices.TryRemove(devicePath, out _);
                _heldKeys.Clear();
            }
        }

        // Key mapping

        private static VirtualKey EvdevToVirtualKey(ushort code)
        {
            return code switch
            {
                30 => VirtualKey.A, 48 => VirtualKey.B, 46 => VirtualKey.C, 32 => VirtualKey.D,
                18 => VirtualKey.E, 33 => VirtualKey.F, 34 => VirtualKey.G, 35 => VirtualKey.H,
                23 => VirtualKey.I, 36 => VirtualKey.J, 37 => VirtualKey.K, 38 => VirtualKey.L,
                50 => VirtualKey.M, 49 => VirtualKey.N, 24 => VirtualKey.O, 25 => VirtualKey.P,
                16 => VirtualKey.Q, 19 => VirtualKey.R, 31 => VirtualKey.S, 20 => VirtualKey.T,
                22 => VirtualKey.U, 47 => VirtualKey.V, 17 => VirtualKey.W, 45 => VirtualKey.X,
                21 => VirtualKey.Y, 44 => VirtualKey.Z,
                2 => VirtualKey.D1, 3 => VirtualKey.D2, 4 => VirtualKey.D3, 5 => VirtualKey.D4,
                6 => VirtualKey.D5, 7 => VirtualKey.D6, 8 => VirtualKey.D7, 9 => VirtualKey.D8,
                10 => VirtualKey.D9, 11 => VirtualKey.D0,
                59 => VirtualKey.F1, 60 => VirtualKey.F2, 61 => VirtualKey.F3, 62 => VirtualKey.F4,
                63 => VirtualKey.F5, 64 => VirtualKey.F6, 65 => VirtualKey.F7, 66 => VirtualKey.F8,
                67 => VirtualKey.F9, 68 => VirtualKey.F10, 87 => VirtualKey.F11, 88 => VirtualKey.F12,
                42 => VirtualKey.LeftShift, 54 => VirtualKey.RightShift,
                29 => VirtualKey.LeftCtrl, 97 => VirtualKey.RightCtrl,
                56 => VirtualKey.LeftAlt, 100 => VirtualKey.RightAlt,
                57 => VirtualKey.Space, 28 => VirtualKey.Enter, 1 => VirtualKey.Escape,
                15 => VirtualKey.Tab, 14 => VirtualKey.Back,
                111 => VirtualKey.Delete, 110 => VirtualKey.Insert,
                102 => VirtualKey.Home, 107 => VirtualKey.End,
                104 => VirtualKey.PageUp, 109 => VirtualKey.PageDown,
                105 => VirtualKey.Left, 103 => VirtualKey.Up, 106 => VirtualKey.Right, 108 => VirtualKey.Down,
                99 => VirtualKey.PrintScreen,
                // Numpad
                82 => VirtualKey.NumPad0, 79 => VirtualKey.NumPad1, 80 => VirtualKey.NumPad2,
                81 => VirtualKey.NumPad3, 75 => VirtualKey.NumPad4, 76 => VirtualKey.NumPad5,
                77 => VirtualKey.NumPad6, 71 => VirtualKey.NumPad7, 72 => VirtualKey.NumPad8,
                73 => VirtualKey.NumPad9,
                // OEM keys
                41 => VirtualKey.OemTilde,        // ` ~
                12 => VirtualKey.OemMinus,        // - _
                13 => VirtualKey.OemPlus,         // = +
                26 => VirtualKey.OemOpenBrackets, // [ {
                27 => VirtualKey.OemCloseBrackets,// ] }
                43 => VirtualKey.OemBackslash,    // \ |
                39 => VirtualKey.OemSemicolon,    // ; :
                40 => VirtualKey.OemQuotes,       // ' "
                51 => VirtualKey.OemComma,        // , <
                52 => VirtualKey.OemPeriod,       // . >
                53 => VirtualKey.OemSlash,        // / ?
                _ => VirtualKey.None
            };
        }

        private static VirtualMouseButton? EvdevToMouseButton(ushort code)
        {
            return code switch
            {
                272 => VirtualMouseButton.Left,     // BTN_LEFT
                273 => VirtualMouseButton.Right,    // BTN_RIGHT
                274 => VirtualMouseButton.Middle,   // BTN_MIDDLE
                275 => VirtualMouseButton.XButton1, // BTN_SIDE
                276 => VirtualMouseButton.XButton2, // BTN_EXTRA
                _ => null
            };
        }

        public bool IsKeyHeld(VirtualKey key) => _heldKeys.ContainsKey(key);

        public void Dispose()
        {
            _cts.Cancel();
            _watcher?.Dispose();
            _cts.Dispose();
        }
    }
}
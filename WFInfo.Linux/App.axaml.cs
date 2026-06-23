using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using WFInfo.Linux.Services;
using WFInfo.Linux.Views;
using WFInfo.Models;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;
using WFInfo.Services.WindowInfo;
using WFInfo.Settings;

namespace WFInfo.Linux
{
    public partial class App : Application
    {
        public static ServiceProvider Services { get; private set; }
        public static MainWindow MainWindowInstance { get; private set; }

        private static SearchItWindow _searchItWindow;
        private static volatile bool _snapItActive;
        public static void ClearSnapItActive() => _snapItActive = false;
        private static SkiaSharp.SKBitmap _nativeSnapItScreenshot;
        /// <summary>Handle selection or cancellation from the native SnapIt overlay.</summary>
        private static void HandleNativeSnapItResult(VulkanLayerService.SnapItResult result)
        {
            _snapItActive = false;

            if (result.Cancelled)
            {
                AppMain.AddLog("Native SnapIt cancelled");
                var shot = System.Threading.Interlocked.Exchange(ref _nativeSnapItScreenshot, null);
                shot?.Dispose();
                return;
            }

            var screenshot = System.Threading.Interlocked.Exchange(ref _nativeSnapItScreenshot, null);
            if (screenshot == null)
            {
                AppMain.AddLog("Native SnapIt: screenshot was null");
                return;
            }

            // Native overlay coordinates and screenshot pixels are both physical.
            // Subtract the game window offset to get screenshot-local coordinates.
            var windowSvc = Services.GetRequiredService<IWindowInfoService>();
            var win = windowSvc.Window;

            int localX = result.X - win.Left;
            int localY = result.Y - win.Top;

            AppMain.AddLog($"Native SnapIt selection: screen=({result.X},{result.Y}) win=({win.Left},{win.Top}) local=({localX},{localY}) {result.Width}x{result.Height} surf={result.SurfW}x{result.SurfH}");

            int sx = localX;
            int sy = localY;
            int sw = result.Width;
            int sh = result.Height;

            sx = Math.Max(0, Math.Min(sx, screenshot.Width - 1));
            sy = Math.Max(0, Math.Min(sy, screenshot.Height - 1));
            sw = Math.Min(sw, screenshot.Width - sx);
            sh = Math.Min(sh, screenshot.Height - sy);

            if (sw < 10 || sh < 10)
            {
                AppMain.StatusUpdate("Selection too small", 2);
                screenshot.Dispose();
                return;
            }

            var cutout = new SkiaSharp.SKBitmap(sw, sh, screenshot.ColorType, screenshot.AlphaType);
            using (var canvas = new SkiaSharp.SKCanvas(cutout))
            {
                canvas.DrawBitmap(screenshot, new SkiaSharp.SKRectI(sx, sy, sx + sw, sy + sh),
                    new SkiaSharp.SKRect(0, 0, sw, sh));
            }

            // Pass game-window-local coordinates as origin (same as Avalonia path),
            // so ProcessSnapIt's overlay positioning math works correctly.
            int originX = localX;
            int originY = localY;

            Task.Run(() =>
            {
                try { OCR.ProcessSnapIt(cutout, screenshot, originX, originY); }
                finally { cutout?.Dispose(); screenshot?.Dispose(); }
            });
        }

        private static void TriggerSnapIt()
        {
            if (_snapItActive) return;

            var screenshot = OCR.CaptureScreenshot();
            if (screenshot == null)
            {
                if (_vulkanLayer?.IsStale != true)
                    AppMain.StatusUpdate("Screenshot failed", 1);
                return;
            }

            if (_vulkanLayer == null)
            {
                AppMain.StatusUpdate("Vulkan layer not available", 1);
                screenshot.Dispose();
                return;
            }

            AppMain.AddLog("Snap-It triggered");
            var old = System.Threading.Interlocked.Exchange(ref _nativeSnapItScreenshot, screenshot);
            old?.Dispose();
            var windowSvc = Services.GetRequiredService<IWindowInfoService>();
            var win = windowSvc.Window;
            _snapItActive = true;
            _vulkanLayer.StartSnapIt(win.Width, win.Height);
        }

        private static VerifyCountWindow _verifyCountWindow;
        private static VulkanLayerService _vulkanLayer;
        private static string _lastStatusMessage;
        private static SocketCommandServer _socketServer;

        internal static void HideNativeOverlays()
        {
            _vulkanLayer?.HideAll();
        }
        private static int _nextSnapItPanelId = 4; // 0-3 reserved for reward panels

        private static volatile bool _nativeRewardsDisplaying;

        private const int MinutesTillAfk = 7; // minutes idle before setting market status to invisible
        private static DateTime _latestActive = DateTime.UtcNow;
        private static bool _userAway;
        private static CancellationTokenSource _manualActivationCts;
        private static string _lastMarketStatus = ApplicationSettings.GlobalSettings.ManualMarketStatus
            ? (ApplicationSettings.GlobalSettings.MarketStatus ?? "ingame") : "invisible";
        private static string _lastMarketStatusBeforeAfk = ApplicationSettings.GlobalSettings.ManualMarketStatus
            ? (ApplicationSettings.GlobalSettings.MarketStatus ?? "ingame") : "invisible";
        private static System.Threading.Timer _afkTimer;

        // Cache reward data from OnRewardDisplay so OnOverlayDisplay can use it
        private static readonly Dictionary<int, (string name, string plat, string setPlat, string ducats, string volume, string owned, bool vaulted, bool mastered, bool hideInfo, bool warning, string highlight)> _rewardData = new();

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            INPC.CaptureUIContext();

            AppMain.Initialize();
            if (Environment.GetCommandLineArgs() is { } clArgs && Array.Exists(clArgs, a => a == "--test-update"))
            {
                AppMain.buildVersion = "0.0.1";
                AppMain.AddLog("TEST MODE: version overridden to 0.0.1 for update testing");
            }
            AppMain.AddLog("Starting WFInfo Linux Desktop");

            CollectStartupDebugInfo();

            var services = new ServiceCollection();

            services.AddSingleton<IReadOnlyApplicationSettings>(ApplicationSettings.GlobalReadonlySettings);
            services.AddSingleton(ApplicationSettings.GlobalSettings);

            services.AddSingleton<ILogger, SimpleLogger>();
            services.AddSingleton<ISoundPlayer, CrossPlatformSoundPlayer>();
            services.AddSingleton<IProcessFinder, LinuxProcessFinder>();
            services.AddSingleton(sp => new VulkanLayerService(sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IWindowInfoService>(sp =>
                new LinuxWindowInfoService(sp.GetRequiredService<VulkanLayerService>(), sp.GetRequiredService<ILogger>()));
            services.AddSingleton<IInputListener, LinuxInputListener>();
            services.AddSingleton<ILogCapture>(sp =>
                new FileLogCapture(sp.GetRequiredService<ILogger>(), sp.GetRequiredService<IProcessFinder>()));

            services.AddSingleton<ITesseractService, TesseractService>();

            services.AddSingleton<Data>(sp =>
            {
                var settings = sp.GetRequiredService<IReadOnlyApplicationSettings>();
                var process = sp.GetRequiredService<IProcessFinder>();
                var window = sp.GetRequiredService<IWindowInfoService>();
                var logCapture = sp.GetService<ILogCapture>();
                return new Data(settings, process, window, logCapture);
            });

            Services = services.BuildServiceProvider();

            // Install Vulkan layer for future game launches
            var logger = Services.GetRequiredService<ILogger>();
            InstallVulkanLayer(logger);

            // Connects lazily, works regardless
            // of whether Warframe or WFInfo starts first.
            _vulkanLayer = Services.GetRequiredService<VulkanLayerService>();
            _vulkanLayer.OnSnapItResult += HandleNativeSnapItResult;
            if (_vulkanLayer.Connect())
                logger.AddLog("VulkanLayerService: connected at startup");
            else
                logger.AddLog("VulkanLayerService: layer socket not yet available, will retry on use");

            _socketServer = new SocketCommandServer(Services.GetRequiredService<ILogger>());
            _socketServer.OnCommand += HandleSocketCommand;
            _socketServer.Start();

            AppMain.OnRunOnUIThread += action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            };

            AppMain.OnStatusUpdate += (message, severity) =>
            {
                _lastStatusMessage = message;
                Dispatcher.UIThread.Post(() => MainWindowInstance?.ChangeStatus(message, severity));
            };

            AppMain.OnSpawnErrorPopup += (timestamp, gap) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var dlg = new ErrorDialogue(timestamp, gap);
                        dlg.Show();
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"Failed to show error popup: {ex.Message}");
                    }
                });
            };

            WireOcrEvents();

            OCR.OnMasterItComplete += () =>
            {
                AppMain.AddLog("Master-It scan complete, equipment data updated");
                Dispatcher.UIThread.Post(() => EquipmentWindow.ReloadIfOpen());
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindowInstance = new MainWindow();
                desktop.MainWindow = MainWindowInstance;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
                desktop.ShutdownRequested += (_, _) => PerformCleanup();

                Task.Run(async () =>
                {
                    try
                    {
                        AppMain.StatusUpdate("Initializing...", 0);

                        AppMain.dataBase = Services.GetRequiredService<Data>();

                        WireSessionEndEvents();

                        var tesseract = Services.GetRequiredService<ITesseractService>();
                        var soundPlayer = Services.GetRequiredService<ISoundPlayer>();
                        var settings = Services.GetRequiredService<IReadOnlyApplicationSettings>();
                        var window = Services.GetRequiredService<IWindowInfoService>();
                        AppMain.StatusUpdate("Initializing OCR engine...", 0);
                        OCR.Init(tesseract, soundPlayer, settings, window, _vulkanLayer);

                        // Enable auto-detection BEFORE database update (don't gate on network)
                        if (settings.Auto)
                        {
                            AppMain.dataBase.EnableLogCapture();
                            AppMain.AddLog("Auto mode enabled, watching game log for rewards");
                        }
                        else
                        {
                            AppMain.AddLog("Auto mode disabled, enable in Settings for automatic reward detection");
                        }

                        // Restore persisted JWT before database update so login state settles immediately
                        try
                        {
                            string storedJwt = WFInfo.Services.EncryptedDataService.LoadStoredJWT();
                            if (storedJwt != null)
                            {
                                AppMain.dataBase.JWT = storedJwt;
                                if (AppMain.dataBase.IsJwtLoggedIn())
                                {
                                    bool connected = await AppMain.dataBase.OpenWebSocket();
                                    AppMain.AddLog(connected
                                        ? "Restored JWT and connected to warframe.market WebSocket"
                                        : "Restored JWT but WebSocket connection failed");

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        if (MainWindowInstance is MainWindow mw)
                                            mw.LoggedIn();
                                    });
                                    StartAfkTimer();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppMain.AddLog($"JWT restore failed: {ex.Message}");
                        }

                        AppMain.StatusUpdate("Updating databases...", 0);
                        await AppMain.dataBase.Update();
                        AppMain.dataBase.FireReloadEnabled(true);

                        WireInputListener(settings);

                        var processFinder = Services.GetRequiredService<IProcessFinder>();
                        if (processFinder.IsRunning && _vulkanLayer != null && !_vulkanLayer.IsConnected && !_vulkanLayer.IsAvailable)
                        {
                            AppMain.StatusUpdate("Vulkan layer not connected, add WFINFO=1 to game launch options and restart the game", 1);
                            AppMain.AddLog("WFInfo initialized but Vulkan layer not available, overlay system won't work");
                        }
                        else if (_vulkanLayer?.IsStale == true)
                        {
                            AppMain.StatusUpdate("Vulkan layer outdated, restart Warframe to apply updates", 1);
                        }
                        else
                        {
                            AppMain.StatusUpdate("WFInfo Initialization Complete", 0);
                        }
                        AppMain.AddLog("WFInfo Linux initialized successfully");

                        processFinder.OnProcessChanged -= OnWarframeProcessChanged;
                        processFinder.OnProcessChanged += OnWarframeProcessChanged;
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"Initialization failed: {ex}");
                        AppMain.StatusUpdate("Initialization failed, check logs", 1);
                    }

                    UpdateDialogue.CheckForUpdates();
                });
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void WireOcrEvents()
        {
            var settings = ApplicationSettings.GlobalReadonlySettings;

            OCR.OnRewardDisplay += (partNumber, name, plat, setPlat, ducats, volume,
                vaulted, mastered, owned, extra, hideInfo, snapIt, highlight) =>
            {
                AppMain.AddLog($"OnRewardDisplay fired: part={partNumber}, name={name}, plat={plat}, ducats={ducats}, display={settings.Display}");

                if (partNumber == 0)
                    _rewardData.Clear();

                _rewardData[partNumber] = (name, plat ?? "0", setPlat, ducats ?? "0", volume ?? "", owned ?? "", vaulted, mastered, hideInfo, snapIt, highlight);

                if (settings.Display == Display.Window)
                {
                    RewardWindow.Instance.LoadPart(partNumber, name, plat ?? "0",
                        setPlat ?? "", ducats ?? "0", volume ?? "", vaulted, mastered,
                        owned ?? "", hideInfo, highlight);
                }
            };

            OCR.OnOverlayDisplay += (partNumber, width, x, y, delay) =>
            {
                _nativeRewardsDisplaying = true;

                if (settings.Display != Display.Overlay)
                    return;

                if (partNumber == 0)
                    Dispatcher.UIThread.Post(() => RewardWindow.DismissIfOpen());

                if (!_rewardData.TryGetValue(partNumber, out var data))
                {
                    AppMain.AddLog($"OverlayDisplay: No reward data for part {partNumber}");
                    return;
                }

                int overlayH = (int)(width * 160.0 / 243.0);
                _vulkanLayer?.ShowOverlay(partNumber, x, y, width, overlayH,
                    data.name, data.plat, data.ducats, data.owned, data.vaulted,
                    volume: data.volume, setPlat: data.setPlat, mastered: data.mastered,
                    warning: data.warning, highlight: data.highlight, delay: delay,
                    hideInfo: data.hideInfo, highContrast: settings.HighContrast);
            };

            // SnapIt overlays, shown regardless of Display mode
            OCR.OnSnapItRewardDisplay += (name, plat, setPlat, ducats, volume, vaulted, mastered,
                owned, partsDetected, hideInfo, warning, width, x, y) =>
            {
                int panelId = _nextSnapItPanelId++;
                if (_nextSnapItPanelId >= 68) _nextSnapItPanelId = 4; // wrap around, max 64 snap-it panels
                int snapW = width;
                int snapH = (int)(snapW * 160.0 / 243.0);

                _vulkanLayer?.ShowOverlay(panelId, x, y, snapW, snapH,
                    name, plat, ducats, owned, vaulted,
                    volume: volume, setPlat: setPlat, mastered: mastered,
                    warning: warning, snapit: true,
                    minEff: settings.MinimumEfficiencyValue,
                    maxEff: settings.MaximumEfficiencyValue,
                    delay: settings.SnapItDelay,
                    hideInfo: hideInfo, highContrast: settings.HighContrast,
                    detected: partsDetected);
            };

            OCR.OnSnapItVerifyCount += (items) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _verifyCountWindow?.Close();
                    _verifyCountWindow = new VerifyCountWindow();
                    _verifyCountWindow.Closed += (_, _) => _verifyCountWindow = null;
                    _verifyCountWindow.ShowVerifyCount(items);
                });
            };

            OCR.OnRewardsDoneDisplaying += () =>
            {
                if (settings.Display == Display.Window)
                    RewardWindow.Instance.FinalizeDisplay();
            };

            OCR.OnClipboardCopy += (text) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var clipboard = TopLevel.GetTopLevel(MainWindowInstance)?.Clipboard;
                        if (clipboard != null)
                            await clipboard.SetTextAsync(text);
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"Clipboard copy failed: {ex.Message}");
                    }
                });
            };

        }

        private void WireInputListener(IReadOnlyApplicationSettings settings)
        {
            try
            {
                var inputListener = Services.GetRequiredService<IInputListener>();

                if (inputListener.StartupWarning != null)
                    AppMain.StatusUpdate(inputListener.StartupWarning, 2);

                inputListener.KeyEvent += (sender, e) =>
                {
                    if (!e.IsDown) return;

                    _latestActive = DateTime.UtcNow.AddMinutes(MinutesTillAfk);

                    if (_snapItActive)
                    {
                        _snapItActive = false;
                        _vulkanLayer?.CancelSnapIt();
                        return;
                    }

                    if (_searchItWindow != null && _searchItWindow.IsInUse)
                    {
                        if (e.Key == VirtualKey.Escape)
                        {
                            Dispatcher.UIThread.Post(() => _searchItWindow?.Finish());
                        }
                        return; // Swallow all keys while search is active
                    }

                    string activationKey = settings.ActivationKey;
                    string pressedKey = e.Key.ToString();

                    bool isActivation = string.Equals(pressedKey, activationKey, StringComparison.OrdinalIgnoreCase) ||
                        (activationKey == "Snapshot" && e.Key == VirtualKey.PrintScreen);

                    if (isActivation)
                    {
                        AppMain.AddLog($"Activation '{pressedKey}', held: Snap({settings.SnapitModifierKey})={inputListener.IsKeyHeld(settings.SnapitModifierKey)}, Search({settings.SearchItModifierKey})={inputListener.IsKeyHeld(settings.SearchItModifierKey)}, Master({settings.MasterItModifierKey})={inputListener.IsKeyHeld(settings.MasterItModifierKey)}");

                        if (inputListener.IsKeyHeld(VirtualKey.Delete))
                        {
                            AppMain.AddLog("Delete + activation: dismissing overlays");
                            HideNativeOverlays();
                            Dispatcher.UIThread.Post(() => RewardWindow.DismissIfOpen());
                            _nativeRewardsDisplaying = false;
                            AppMain.StatusUpdate("Overlays dismissed", 1);
                            return;
                        }

                        if (settings.Debug && inputListener.IsKeyHeld(settings.DebugModifierKey))
                        {
                            if (inputListener.IsKeyHeld(settings.SnapitModifierKey))
                            {
                                AppMain.AddLog("Debug: LoadScreenshot for SnapIt");
                                AppMain.StatusUpdate("Offline testing with screenshot for snapit", 0);
                                LoadScreenshotDebug("snapit");
                            }
                            else if (inputListener.IsKeyHeld(settings.MasterItModifierKey))
                            {
                                AppMain.AddLog("Debug: LoadScreenshot for MasterIt");
                                AppMain.StatusUpdate("Offline testing with screenshot for masterit", 0);
                                LoadScreenshotDebug("masterit");
                            }
                            else
                            {
                                AppMain.AddLog("Debug: LoadScreenshot for rewards");
                                AppMain.StatusUpdate("Offline testing with screenshot", 0);
                                LoadScreenshotDebug("normal");
                            }
                            return;
                        }

                        if (inputListener.IsKeyHeld(settings.SnapitModifierKey))
                        {
                            AppMain.AddLog("Snap-It triggered");
                            TriggerSnapIt();
                            return;
                        }
                        if (inputListener.IsKeyHeld(settings.SearchItModifierKey))
                        {
                            AppMain.AddLog("Search-It triggered");
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_searchItWindow == null)
                                {
                                    _searchItWindow = new SearchItWindow();
                                    _searchItWindow.Closed += (_, _) => _searchItWindow = null;
                                }
                                _searchItWindow.Start();
                            });
                            return;
                        }
                        if (inputListener.IsKeyHeld(settings.MasterItModifierKey))
                        {
                            AppMain.AddLog("Master-It triggered");
                            AppMain.StatusUpdate("Starting Master-It scan...", 0);
                            Task.Run(() =>
                            {
                                var screenshot = OCR.CaptureScreenshot();
                                if (screenshot == null) { if (_vulkanLayer?.IsStale != true) AppMain.StatusUpdate("Screenshot failed", 1); return; }
                                using (screenshot) { OCR.ProcessProfileScreen(screenshot); }
                            });
                            return;
                        }
                    }

                    if (isActivation)
                    {
                        var process = Services.GetRequiredService<IProcessFinder>();
                        if (!process.IsRunning && !settings.Debug)
                        {
                            AppMain.AddLog("Activation key ignored, Warframe not running (enable Debug to override)");
                            return;
                        }

                        // No modifier held, plain scan rewards
                        AppMain.AddLog("Activation key pressed: " + pressedKey);
                        var oldCts = _manualActivationCts;
                        _manualActivationCts = new CancellationTokenSource();
                        var kbCts = _manualActivationCts;
                        oldCts?.Cancel();
                        oldCts?.Dispose();
                        Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(1000, kbCts.Token);
                                OCR.ProcessRewardScreen();
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                AppMain.AddLog($"OCR processing failed: {ex.Message}");
                                AppMain.StatusUpdate("Processing failed", 1);
                            }
                        });
                    }
                };

                inputListener.MouseEvent += (sender, e) =>
                {
                    _latestActive = DateTime.UtcNow.AddMinutes(MinutesTillAfk);

                    if (e.Button == VirtualMouseButton.Left && _nativeRewardsDisplaying)
                    {
                        if (settings.Display != Display.Overlay && !settings.AutoList && !settings.AutoCSV && !settings.AutoCount)
                        {
                            _nativeRewardsDisplaying = false;
                            return;
                        }

                        Task.Run(() =>
                        {
                            try
                            {
                                var cursorPos = GetCursorPosition();
                                if (cursorPos == null) return;
                                int index = OCR.GetSelectedReward(cursorPos.Value.X, cursorPos.Value.Y);
                                AppMain.AddLog("Chosen reward index: " + index);
                                if (index < 0) return;
                                AppMain.dataBase.SelectedRewardIndex = (short)index;
                            }
                            catch (Exception ex)
                            {
                                AppMain.AddLog($"Reward selection failed: {ex.Message}");
                            }
                        });
                        return;
                    }

                    // Mouse-button activation (e.g., Middle, XButton1, XButton2)
                    var activationMouse = settings.ActivationMouseButton;
                    if (activationMouse == null || e.Button != activationMouse.Value) return;

                    var process = Services.GetRequiredService<IProcessFinder>();
                    if (!process.IsRunning && !settings.Debug)
                    {
                        AppMain.AddLog("Mouse activation ignored, Warframe not running");
                        return;
                    }

                    AppMain.AddLog($"Mouse activation: {e.Button}");
                    var oldCts = _manualActivationCts;
                    _manualActivationCts = new CancellationTokenSource();
                    var mouseCts = _manualActivationCts;
                    oldCts?.Cancel();
                    oldCts?.Dispose();
                    Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000, mouseCts.Token);
                            OCR.ProcessRewardScreen();
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            AppMain.AddLog($"OCR processing failed: {ex.Message}");
                            AppMain.StatusUpdate("Processing failed", 1);
                        }
                    });
                };

                AppMain.AddLog("Input listener wired for activation key: " + settings.ActivationKey);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to wire input listener: {ex.Message}");
            }
        }

        private static void HandleSocketCommand(string cmd)
        {
            _latestActive = DateTime.UtcNow.AddMinutes(MinutesTillAfk);

            var settings = ApplicationSettings.GlobalReadonlySettings;

            if (OCR.processingActive || _snapItActive)
            {
                AppMain.AddLog($"SocketCommand '{cmd}' ignored, already processing");
                return;
            }

            switch (cmd)
            {
                case "activate":
                {
                    var process = Services.GetRequiredService<IProcessFinder>();
                    if (!process.IsRunning && !settings.Debug)
                    {
                        AppMain.AddLog("Socket activate ignored, Warframe not running");
                        return;
                    }
                    AppMain.AddLog("Socket: activate (reward scan)");
                    var oldCts = _manualActivationCts;
                    _manualActivationCts = new CancellationTokenSource();
                    var sockCts = _manualActivationCts;
                    oldCts?.Cancel();
                    oldCts?.Dispose();
                    Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000, sockCts.Token);
                            OCR.ProcessRewardScreen();
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            AppMain.AddLog($"Socket activate failed: {ex.Message}");
                            AppMain.StatusUpdate("Processing failed", 1);
                        }
                    });
                    break;
                }
                case "snapit":
                {
                    AppMain.AddLog("Socket: snapit");
                    TriggerSnapIt();
                    break;
                }
                case "searchit":
                {
                    AppMain.AddLog("Socket: searchit");
                    LaunchSearchIt();
                    break;
                }
                case "masterit":
                {
                    AppMain.AddLog("Socket: masterit");
                    AppMain.StatusUpdate("Starting Master-It scan...", 0);
                    Task.Run(() =>
                    {
                        var screenshot = OCR.CaptureScreenshot();
                        if (screenshot == null) { if (_vulkanLayer?.IsStale != true) AppMain.StatusUpdate("Screenshot failed", 1); return; }
                        using (screenshot) { OCR.ProcessProfileScreen(screenshot); }
                    });
                    break;
                }
                default:
                    AppMain.AddLog($"SocketCommand: unknown command '{cmd}'");
                    break;
            }
        }

        private static AutoCountWindow _autoCountWindow;
        private static ListingHelperWindow _listingHelperWindow;
        private static readonly CultureInfo _csvCulture = CultureInfo.InvariantCulture;

        private void WireSessionEndEvents()
        {
            var settings = ApplicationSettings.GlobalReadonlySettings;

            AppMain.dataBase.OnSessionEnd += (rewards, selectedIdx) =>
            {
                _nativeRewardsDisplaying = false;

                Task.Run(async () =>
                {
                    AppMain.AddLog($"Session end: AutoCSV={settings.AutoCSV}, AutoCount={settings.AutoCount}, AutoList={settings.AutoList}");

                    if (settings.AutoCSV)
                    {
                        try
                        {
                            string csvPath = Path.Combine(PlatformPaths.AppDataPath, "rewardExport.csv");
                            string csv = "";

                            if (!File.Exists(csvPath))
                                csv += "Timestamp,ChosenIndex,Reward_0_Name,Reward_0_Plat,Reward_0_Ducats,Reward_1_Name,Reward_1_Plat,Reward_1_Ducats,Reward_2_Name,Reward_2_Plat,Reward_2_Ducats,Reward_3_Name,Reward_3_Plat,Reward_3_Ducats" + Environment.NewLine;

                            foreach (var screen in rewards)
                            {
                                csv += DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff", _csvCulture) + "," + selectedIdx;
                                for (int i = 0; i < 4; i++)
                                {
                                    if (i < screen.Count)
                                    {
                                        string itemName = screen[i];
                                        string plat = "0";
                                        string ducats = "0";
                                        if (AppMain.dataBase.marketData != null
                                            && AppMain.dataBase.marketData.TryGetValue(itemName, out JToken marketToken))
                                        {
                                            plat = marketToken["plat"]?.ToObject<string>() ?? "0";
                                            ducats = marketToken["ducats"]?.ToObject<string>() ?? "0";
                                        }
                                        csv += "," + itemName + "," + plat + "," + ducats;
                                    }
                                    else
                                    {
                                        csv += ",\"\",0,0";
                                    }
                                }
                                csv += Environment.NewLine;
                            }

                            File.AppendAllText(csvPath, csv);
                            AppMain.AddLog("AutoCSV: appended " + rewards.Count + " row(s) to rewardExport.csv");
                        }
                        catch (Exception ex)
                        {
                            AppMain.AddLog($"AutoCSV failed: {ex.Message}");
                        }
                    }

                    if (settings.AutoCount)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_autoCountWindow == null)
                            {
                                _autoCountWindow = new AutoCountWindow();
                                _autoCountWindow.Closed += (_, _) => _autoCountWindow = null;
                            }
                            _autoCountWindow.AddRewards(rewards, selectedIdx);
                            if (!_autoCountWindow.IsVisible)
                            {
                                _autoCountWindow.Show();
                                _autoCountWindow.Activate();
                            }
                        });
                    }

                    if (settings.AutoList)
                    {
                        bool jwtOk = AppMain.dataBase.IsJwtLoggedIn() && await AppMain.dataBase.IsJWTvalid();
                        if (!jwtOk)
                        {
                            AppMain.dataBase.Disconnect();
                            AppMain.AddLog("AutoList: Not logged in or JWT expired, disconnected");
                            AppMain.StatusUpdate("AutoList requires warframe.market login", 1);
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_listingHelperWindow == null || !_listingHelperWindow.IsVisible)
                                {
                                    _listingHelperWindow = new ListingHelperWindow();
                                    _listingHelperWindow.Closed += (_, _) => _listingHelperWindow = null;
                                }
                                _listingHelperWindow.LoadRewards(rewards, selectedIdx);
                                _listingHelperWindow.Show();
                                _listingHelperWindow.Activate();
                            });
                        }
                    }
                });
            };
        }

        /// <summary>
        /// Launch the SearchIt window from UI (MainWindow sidebar button).
        /// </summary>
        public static void LaunchSearchIt()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_searchItWindow == null)
                {
                    _searchItWindow = new SearchItWindow();
                    _searchItWindow.Closed += (_, _) => _searchItWindow = null;
                }
                _searchItWindow.Start();
            });
        }

        /// <summary>
        /// Show the listing helper window with the given rewards.
        /// Called from SearchIt to open integrated listing.
        /// </summary>
        public static void ShowListingHelper(List<List<string>> rewards, short selectedIdx)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_listingHelperWindow == null || !_listingHelperWindow.IsVisible)
                {
                    _listingHelperWindow = new ListingHelperWindow();
                    _listingHelperWindow.Closed += (_, _) => _listingHelperWindow = null;
                }
                _listingHelperWindow.LoadRewards(rewards, selectedIdx);
                _listingHelperWindow.Show();
                _listingHelperWindow.Activate();
            });
        }

        private void TrayShow_Click(object sender, EventArgs e)
        {
            if (MainWindowInstance != null)
            {
                MainWindowInstance.Show();
                MainWindowInstance.WindowState = WindowState.Normal;
                MainWindowInstance.Activate();
            }
        }

        private void TrayWiki_Click(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://wfinfo.warframestat.us/") { UseShellExecute = true }); }
            catch { }
        }

        private void TrayBugs_Click(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/wuuthradd/WFInfo-Linux/issues") { UseShellExecute = true }); }
            catch { }
        }

        private void TrayClose_Click(object sender, EventArgs e)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        private static int _cleanedUp;

        internal static void PerformCleanup()
        {
            if (System.Threading.Interlocked.Exchange(ref _cleanedUp, 1) != 0)
                return;

            try { AppMain.dataBase?.DisableLogCapture(); } catch { }
            try { Services?.GetService<ILogCapture>()?.Dispose(); } catch { }

            try
            {
                if (AppMain.dataBase != null)
                {
                    if (AppMain.dataBase.rememberMe && AppMain.dataBase.JWT != null)
                        WFInfo.Services.EncryptedDataService.PersistJWT(AppMain.dataBase.JWT);
                }
            }
            catch { }

            try { _afkTimer?.Dispose(); _afkTimer = null; } catch { }
            try { _socketServer?.Dispose(); _socketServer = null; } catch { }

            CleanupOverlays();
            AppMain.FlushLog();
            try { ApplicationSettings.GlobalSettings.Save(); } catch { }

            try { Services?.Dispose(); } catch { }
        }

        private static void CleanupOverlays()
        {
            _vulkanLayer?.Dispose();
        }

        /// <summary>Install the Vulkan implicit layer .so and manifest to user-local paths.</summary>
        private static void InstallVulkanLayer(ILogger logger)
        {
            try
            {
                string layerSo = FindLayerFile("libwfinfo_vk.so");
                string layerJson = FindLayerFile("wfinfo_vk.json");
                if (layerSo == null || layerJson == null)
                {
                    logger.AddLog("VulkanLayer: layer files not found, skipping install");
                    return;
                }

                string installDir = PlatformPaths.AppDataPath;
                string installedSo = Path.Combine(installDir, "libwfinfo_vk.so");

                if (!File.Exists(installedSo) || !FilesEqual(layerSo, installedSo))
                {
                    Directory.CreateDirectory(installDir);
                    // Atomic replace: copy to temp then rename so a running game
                    // keeps the old inode mapped and new launches get the new file.
                    string tmpSo = installedSo + ".tmp";
                    File.Copy(layerSo, tmpSo, overwrite: true);
                    File.Move(tmpSo, installedSo, overwrite: true);
                    logger.AddLog("VulkanLayer: installed libwfinfo_vk.so");
                }

                string localDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "vulkan", "implicit_layer.d");
                Directory.CreateDirectory(localDir);
                string localManifest = Path.Combine(localDir, "wfinfo_vk.json");

                string manifest = File.ReadAllText(layerJson)
                    .Replace("\"./libwfinfo_vk.so\"", $"\"{installedSo}\"");

                if (!File.Exists(localManifest) || File.ReadAllText(localManifest) != manifest)
                {
                    File.WriteAllText(localManifest, manifest);
                    logger.AddLog($"VulkanLayer: installed manifest to {localManifest}");
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"VulkanLayer: install failed: {ex.Message}");
            }
        }

        private static string FindLayerFile(string name)
        {
            string baseDir = AppContext.BaseDirectory;
            foreach (string candidate in new[] {
                Path.Combine(baseDir, name),
                Path.Combine(baseDir, "..", "lib", name),
                Path.Combine(baseDir, "..", "..", "..", "..", "WFInfo.Linux", "NativeOverlay", name),
            })
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }

        private static bool FilesEqual(string a, string b)
        {
            using var ha = SHA256.Create();
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            byte[] da = ha.ComputeHash(fa);
            byte[] db = ha.ComputeHash(fb);
            if (da.Length != db.Length) return false;
            for (int i = 0; i < da.Length; i++)
                if (da[i] != db[i]) return false;
            return true;
        }

        private static void CollectStartupDebugInfo()
        {
            try
            {
                AppMain.AddLog("=== System Info ===");

                if (File.Exists("/etc/os-release"))
                {
                    foreach (var line in File.ReadAllLines("/etc/os-release"))
                    {
                        if (line.StartsWith("PRETTY_NAME="))
                        {
                            AppMain.AddLog("OS: " + line.Substring(12).Trim('"'));
                            break;
                        }
                    }
                }

                AppMain.AddLog("Kernel: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);

                if (File.Exists("/proc/cpuinfo"))
                {
                    foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
                    {
                        if (line.StartsWith("model name"))
                        {
                            AppMain.AddLog("CPU: " + line.Substring(line.IndexOf(':') + 1).Trim());
                            break;
                        }
                    }
                }

                AppMain.AddLog("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

                string waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
                string x11Display = Environment.GetEnvironmentVariable("DISPLAY");
                string sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
                AppMain.AddLog($"Display: session={sessionType ?? "?"}, WAYLAND_DISPLAY={waylandDisplay ?? "(none)"}, DISPLAY={x11Display ?? "(none)"}");

                string de = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
                if (!string.IsNullOrEmpty(de))
                    AppMain.AddLog("Desktop: " + de);

                // GPU info from DRM subsystem
                string gpuInfo = "unknown";
                try
                {
                    var gpuParts = new System.Collections.Generic.List<string>();
                    foreach (string cardDir in Directory.GetDirectories("/sys/class/drm", "card*"))
                    {
                        string cn = Path.GetFileName(cardDir);
                        if (cn.Length < 5 || !char.IsDigit(cn[4]) || cn.Contains('-'))
                            continue;

                        string deviceDir = Path.Combine(cardDir, "device");
                        if (!Directory.Exists(deviceDir)) continue;

                        string vendorFile = Path.Combine(deviceDir, "vendor");
                        string deviceFile = Path.Combine(deviceDir, "device");
                        if (!File.Exists(vendorFile)) continue;

                        string vendor = File.ReadAllText(vendorFile).Trim();
                        string deviceId = File.Exists(deviceFile) ? File.ReadAllText(deviceFile).Trim() : "?";
                        string drmDriver = "";
                        string driverLink = Path.Combine(deviceDir, "driver");
                        var driverDirInfo = new DirectoryInfo(driverLink);
                        if (driverDirInfo.Exists && driverDirInfo.LinkTarget != null)
                            drmDriver = Path.GetFileName(driverDirInfo.LinkTarget);

                        gpuParts.Add($"{cn}: vendor={vendor} device={deviceId} driver={drmDriver}");
                    }
                    if (gpuParts.Count > 0)
                        gpuInfo = string.Join(", ", gpuParts);
                }
                catch { }
                AppMain.AddLog("GPU: " + gpuInfo);

                AppMain.AddLog("=== End System Info ===");
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Debug info collection failed: {ex.Message}");
            }
        }

        private static void LoadScreenshotDebug(string mode)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var topLevel = TopLevel.GetTopLevel(MainWindowInstance);
                    if (topLevel == null) return;

                    var imageFilter = new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.bmp" }
                    };
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Load Screenshot for Debug",
                            AllowMultiple = true,
                            FileTypeFilter = new[] { imageFilter, Avalonia.Platform.Storage.FilePickerFileTypes.All }
                        });
                    if (files == null || files.Count == 0) return;

                    var windowService = Services.GetRequiredService<IWindowInfoService>();
                    var filePaths = new List<string>();
                    foreach (var f in files)
                    {
                        var path = f.Path?.LocalPath;
                        if (!string.IsNullOrEmpty(path))
                            filePaths.Add(path);
                    }

                    await Task.Run(() =>
                    {
                        foreach (string file in filePaths)
                        {
                            try
                            {
                                AppMain.AddLog($"Debug: Testing file: {file}");
                                using var stream = System.IO.File.OpenRead(file);
                                using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
                                if (bitmap == null)
                                {
                                    AppMain.AddLog($"Debug: Failed to decode {file}");
                                    continue;
                                }

                                windowService.UseImage(bitmap);

                                switch (mode)
                                {
                                    case "normal":
                                        OCR.ProcessRewardScreen(bitmap);
                                        break;
                                    case "snapit":
                                        OCR.ProcessSnapIt(bitmap, bitmap, 0, 0);
                                        break;
                                    case "masterit":
                                        OCR.ProcessProfileScreen(bitmap);
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                AppMain.AddLog($"Debug: Error processing {file}: {ex.Message}");
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"Debug: LoadScreenshot failed: {ex.Message}");
                }
            });
        }

        public static void StartAfkTimer()
        {
            _latestActive = DateTime.UtcNow.AddMinutes(1);
            _userAway = false;
            _afkTimer?.Dispose();
            _afkTimer = new System.Threading.Timer(_ => AfkTimeoutCheck(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            AppMain.AddLog("AFK timer started");
        }

        private static async void OnWarframeProcessChanged(Process proc)
        {
            try
            {
                if (proc != null && _vulkanLayer != null)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (_vulkanLayer.Reconnect() && !_vulkanLayer.IsStale)
                    {
                        AppMain.AddLog("VulkanLayerService: reconnected after game restart");
                        if (_lastStatusMessage != null &&
                            (_lastStatusMessage.Contains("WFINFO=1") || _lastStatusMessage.Contains("outdated")))
                            AppMain.StatusUpdate("WFInfo Initialization Complete", 0);
                    }
                }

                if (proc != null && AppMain.dataBase != null && AppMain.dataBase.IsJwtLoggedIn())
                {
                    var settings = Services?.GetService<IReadOnlyApplicationSettings>();
                    string status = (settings?.ManualMarketStatus == true)
                        ? (settings.MarketStatus ?? "ingame") : "ingame";
                    await AppMain.dataBase.SetWebsocketStatus(status);
                    _lastMarketStatus = status;
                    AppMain.AddLog($"Warframe detected, WFM status set to {status}");
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to update WFM status on process change: " + ex.Message);
            }
        }

        public static void OnMarketStatusChanged(string status)
        {
            if (!_userAway)
                _lastMarketStatus = status;
        }

        private static async void AfkTimeoutCheck()
        {
            try
            {
                if (AppMain.dataBase == null || !AppMain.dataBase.IsJwtLoggedIn())
                    return;

                var process = Services?.GetService<IProcessFinder>();
                DateTime now = DateTime.UtcNow;

                if (process != null && !process.IsRunning && _lastMarketStatus != "invisible")
                {
                    AppMain.dataBase.DisableLogCapture();
                    var settings = Services?.GetService<IReadOnlyApplicationSettings>();
                    if (settings != null && settings.Auto)
                        AppMain.dataBase.EnableLogCapture();

                    string closeStatus = (settings?.ManualMarketStatus == true)
                        ? (settings.MarketStatus ?? "invisible") : "invisible";
                    _lastMarketStatus = closeStatus;
                    await AppMain.dataBase.SetWebsocketStatus(closeStatus);
                    AppMain.AddLog($"Warframe closed, WFM status set to {closeStatus}");
                    AppMain.StatusUpdate($"WFM status set {closeStatus}, Warframe was closed", 0);
                    return;
                }

                var inputListener = Services?.GetService<IInputListener>();
                bool hasInputAccess = inputListener != null && inputListener.StartupWarning == null;

                if (hasInputAccess)
                {
                    if (_userAway && _latestActive > now)
                    {
                        _userAway = false;
                        if (_lastMarketStatusBeforeAfk != "invisible")
                        {
                            await AppMain.dataBase.SetWebsocketStatus(_lastMarketStatusBeforeAfk);
                            string user = string.IsNullOrEmpty(AppMain.dataBase.inGameName) ? "user" : AppMain.dataBase.inGameName;
                            AppMain.StatusUpdate($"Welcome back {user}, restored as {_lastMarketStatusBeforeAfk}", 0);
                        }
                        else
                        {
                            AppMain.StatusUpdate("Welcome back", 0);
                        }
                    }
                    else if (!_userAway && _latestActive <= now)
                    {
                        _lastMarketStatusBeforeAfk = _lastMarketStatus;
                        _userAway = true;
                        if (_lastMarketStatus != "invisible")
                        {
                            await AppMain.dataBase.SetWebsocketStatus("invisible");
                            AppMain.StatusUpdate($"User has been inactive for {MinutesTillAfk} minutes", 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"AFK timeout check failed: {ex.Message}");
            }
        }

        private static PixelPoint? GetCursorPosition()
        {
            // Query cursor via DBMON bridge
            var logCapture = Services?.GetService<ILogCapture>() as FileLogCapture;
            var pos = logCapture?.QueryCursorPosition();
            if (pos == null)
                AppMain.AddLog("Cursor query failed: DBMON did not respond");
            return pos;
        }
    }
}

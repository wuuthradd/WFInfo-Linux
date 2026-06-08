using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class MainWindow : Window
    {
        private SettingsWindow _settingsWindow;
        private RelicsWindow _relicsWindow;
        private EquipmentWindow _equipmentWindow;
        private bool _dataEventsWired = false;
        private bool _updateSuppression = false; // Suppress ComboBox events during programmatic updates

        public MainWindow()
        {
            InitializeComponent();
            Version.Text = "v" + AppMain.BuildVersion;
            AppMain.AddLog("MainWindow created");

            if (!WFInfo.Settings.ApplicationSettings.GlobalSettings.Initialized)
            {
                WFInfo.Settings.ApplicationSettings.GlobalSettings.Initialized = true;
                Opened += (_, _) =>
                {
                    var welcome = new WelcomeDialogue();
                    var s = DesktopScaling;
                    welcome.Position = new Avalonia.PixelPoint(
                        (int)(Position.X + Width * s + 30 * s),
                        (int)(Position.Y + Height * s / 2 - welcome.Height * s / 2));
                    welcome.Show();
                };
            }

            TryWireDataEvents();

            // Also listen for status updates to re-try wiring once data is loaded
            AppMain.OnStatusUpdate += (msg, _) =>
            {
                if (!_dataEventsWired && AppMain.dataBase != null)
                    Dispatcher.UIThread.Post(TryWireDataEvents);
            };
        }

        private void TryWireDataEvents()
        {
            if (_dataEventsWired || AppMain.dataBase == null)
                return;

            AppMain.dataBase.OnMarketDataUpdated += text =>
                Dispatcher.UIThread.Post(() => MarketData.Text = text);
            AppMain.dataBase.OnDropDataUpdated += text =>
                Dispatcher.UIThread.Post(() => DropData.Text = text);

            AppMain.dataBase.OnReloadEnabled += enabled =>
                Dispatcher.UIThread.Post(() => ReloadBtn.IsEnabled = enabled);

            AppMain.dataBase.OnWebSocketStatusChanged += status =>
            {
                App.OnMarketStatusChanged(status);
                Dispatcher.UIThread.Post(() => UpdateMarketStatus(status));
            };

            // Enable login button once data is loaded
            Dispatcher.UIThread.Post(() => LoginBtn.IsEnabled = true);

            _dataEventsWired = true;
            AppMain.AddLog("Data events wired to MainWindow");
        }

        private static readonly SolidColorBrush StatusNormal = new(Color.Parse("#FFB1D0D9"));
        private static readonly SolidColorBrush StatusError = new(Colors.Red);
        private static readonly SolidColorBrush StatusWarning = new(Colors.Orange);
        private static readonly SolidColorBrush StatusInfo = new(Colors.Yellow);

        public void ChangeStatus(string message, int severity)
        {
            Status.Text = message;
            Status.Foreground = severity switch
            {
                0 => StatusNormal,
                1 => StatusError,
                2 => StatusWarning,
                _ => StatusInfo,
            };
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Avalonia.Visual)sender);
                if (pos.Y > 22) return;
                for (var src = e.Source as Avalonia.StyledElement; src != null && src != this; src = src.Parent)
                    if (src is ComboBox) return;
                try
                {
                    BeginMoveDrag(e);
                }
                catch (InvalidOperationException)
                {
                    // Tiling WMs (i3/Sway) or rapid pointer release can throw
                }
            }
        }

        private void Minimise(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void WebsiteClick(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://discord.gg/N8S5zfw");
        }

        private void RelicsClick(object sender, RoutedEventArgs e)
        {
            if (AppMain.dataBase?.relicData == null)
            {
                ChangeStatus("Relic data not yet loaded in", 2);
                return;
            }
            AppMain.AddLog("Relics window requested");
            try
            {
                if (_relicsWindow == null || !_relicsWindow.IsVisible)
                {
                    _relicsWindow = new RelicsWindow();
                    _relicsWindow.Closed += (_, _) => _relicsWindow = null;
                    _relicsWindow.Show();
                }
                else
                {
                    _relicsWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to open Relics window: " + ex.Message);
            }
        }

        private void EquipmentClick(object sender, RoutedEventArgs e)
        {
            if (AppMain.dataBase?.equipmentData == null)
            {
                ChangeStatus("Equipment data not yet loaded in", 2);
                return;
            }
            AppMain.AddLog("Equipment window requested");
            try
            {
                if (_equipmentWindow == null)
                {
                    _equipmentWindow = new EquipmentWindow();
                    _equipmentWindow.Closing += (_, e) =>
                    {
                        e.Cancel = true;
                        _equipmentWindow.Hide();
                    };
                }
                _equipmentWindow.Show();
                _equipmentWindow.Activate();
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to open Equipment window: " + ex.Message);
            }
        }

        private void SettingsClick(object sender, RoutedEventArgs e)
        {
            AppMain.AddLog("Settings window requested");
            try
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow();
                    _settingsWindow.Closing += (_, e) =>
                    {
                        e.Cancel = true;
                        _settingsWindow.Hide();
                    };
                }
                // WPF: Settings snaps directly below MainWindow, left-aligned
                _settingsWindow.Position = new Avalonia.PixelPoint(
                    Position.X,
                    (int)(Position.Y + Bounds.Height * DesktopScaling));
                _settingsWindow.Show();
                _settingsWindow.Activate();
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to open Settings window: " + ex.Message);
            }
        }

        private void ReloadDataClick(object sender, RoutedEventArgs e)
        {
            AppMain.AddLog("Data reload requested");
            ReloadBtn.IsEnabled = false;
            MarketData.Text = "Loading...";
            DropData.Text = "Loading...";
            ChangeStatus("Forcing Data Update", 0);
            if (AppMain.dataBase != null)
                Task.Run(async () => await AppMain.dataBase.ForceDataUpdate());
        }

        private LoginWindow _loginWindow;

        private void SpawnLogin(object sender, RoutedEventArgs e)
        {
            AppMain.AddLog("Login requested");
            try
            {
                if (_loginWindow == null || !_loginWindow.IsVisible)
                {
                    _loginWindow = new LoginWindow();
                    _loginWindow.Closed += (_, _) => _loginWindow = null;
                }
                _loginWindow.MoveLogin(Position.X + Width * DesktopScaling, Position.Y);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to open Login window: " + ex.Message);
            }
        }

        public void LoggedIn()
        {
            LoginBtn.IsVisible = false;
            StatusCombo.SelectedIndex = 1; // "Online" default
            StatusCombo.IsVisible = true;
            CreateListingBtn.IsVisible = true;
            PlusOneBtn.IsVisible = true;
            SearchItBtn.IsVisible = true;
            ChangeStatus("Logged in", 0);
        }

        public void SignOut()
        {
            LoginBtn.IsVisible = true;
            StatusCombo.IsVisible = false;
            CreateListingBtn.IsVisible = false;
            PlusOneBtn.IsVisible = false;
            SearchItBtn.IsVisible = false;
        }

        private void LoggOut()
        {
            SignOut();
            Task.Run(() => AppMain.dataBase?.Disconnect());
        }

        private void UpdateMarketStatus(string status)
        {
            _updateSuppression = true;
            switch (status)
            {
                case "online":
                    if (StatusCombo.SelectedIndex != 1) StatusCombo.SelectedIndex = 1;
                    break;
                case "invisible":
                    if (StatusCombo.SelectedIndex != 2) StatusCombo.SelectedIndex = 2;
                    break;
                case "ingame":
                    if (StatusCombo.SelectedIndex != 0) StatusCombo.SelectedIndex = 0;
                    break;
            }
            _updateSuppression = false;
        }

        private void StatusComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updateSuppression || !StatusCombo.IsVisible) return;

            string[] statuses = { "ingame", "online", "invisible" };
            int idx = StatusCombo.SelectedIndex;
            if (idx >= 0 && idx < statuses.Length)
            {
                string status = statuses[idx];
                App.OnMarketStatusChanged(status);
                var settings = ApplicationSettings.GlobalSettings;
                settings.MarketStatus = status;
                settings.Save();
                Task.Run(async () => await AppMain.dataBase.SetWebsocketStatus(status));
            }

            switch (StatusCombo.SelectedIndex)
            {
                case 3: // Log out
                    LoggOut();
                    // Delete stored JWT on explicit logout
                    try
                    {
                        string jwtPath = Path.Combine(AppMain.AppPath, "jwt_encrypted");
                        if (File.Exists(jwtPath)) File.Delete(jwtPath);
                    }
                    catch { }
                    break;
            }
        }

        private void OpenAppDataFolder(object sender, PointerPressedEventArgs e)
        {
            OpenUrl(AppMain.AppPath);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private void CreateListing_Click(object sender, RoutedEventArgs e)
        {
            if (OCR.processingActive)
            {
                AppMain.StatusUpdate("Still Processing Reward Screen", 2);
                return;
            }

            if (AppMain.dataBase == null || !AppMain.dataBase.IsJwtLoggedIn())
            {
                AppMain.StatusUpdate("Please log in first", 1);
                return;
            }

            if (AppMain.dataBase.PrimeRewards == null || AppMain.dataBase.PrimeRewards.Count == 0)
            {
                AppMain.StatusUpdate("No recorded rewards found", 2);
                return;
            }

            AppMain.AddLog($"CreateListing: opening listing helper with {AppMain.dataBase.PrimeRewards.Count} reward screen(s)");
            App.ShowListingHelper(
                new List<List<string>>(AppMain.dataBase.PrimeRewards),
                AppMain.dataBase.SelectedRewardIndex);

            AppMain.dataBase.PrimeRewards.Clear();
            AppMain.dataBase.SelectedRewardIndex = 0;
        }

        private void SearchIt_Click(object sender, RoutedEventArgs e)
        {
            if (OCR.processingActive)
            {
                AppMain.StatusUpdate("Still Processing Reward Screen", 2);
                return;
            }
            AppMain.AddLog("Starting search it");
            AppMain.StatusUpdate("Starting search it", 0);
            App.LaunchSearchIt();
        }

        private PlusOneWindow _plusOneWindow;

        private void PlusOne_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_plusOneWindow == null || !_plusOneWindow.IsVisible)
                {
                    _plusOneWindow = new PlusOneWindow();
                    _plusOneWindow.Closed += (_, _) => _plusOneWindow = null;
                }
                _plusOneWindow.Position = new Avalonia.PixelPoint(
                    (int)(Position.X + Width * DesktopScaling), Position.Y);
                _plusOneWindow.Show();
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to open PlusOne window: {ex.Message}");
            }
        }
    }
}
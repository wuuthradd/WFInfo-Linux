using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WFInfo.Models;
using WFInfo.Services.WindowInfo;
using WFInfo.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace WFInfo.Linux.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly ApplicationSettings _settings;
        private enum KeyCaptureTarget { None, Activation, SearchIt, SnapIt, MasterIt }
        private KeyCaptureTarget _captureTarget = KeyCaptureTarget.None;
        private bool _loading = true;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = ApplicationSettings.GlobalSettings;
            _loading = true;
            PopulateThemeCombo();
            LoadSettings();
            _loading = false;
            KeyDown += OnKeyDown;
            UpdateKeyBindingVisibility();
        }

        private void PopulateThemeCombo()
        {
            ThemeCombo.Items.Clear();
            foreach (var val in Enum.GetValues(typeof(WFtheme)))
                ThemeCombo.Items.Add(new ComboBoxItem { Content = val.ToString(), Tag = val });
        }

        private void LoadSettings()
        {
            switch (_settings.Display)
            {
                case Display.Overlay: OverlayRadio.IsChecked = true; break;
                case Display.Window: WindowRadio.IsChecked = true; break;
                case Display.Light: LightRadio.IsChecked = true; break;
            }

            OverlayOffsetSection.IsVisible = _settings.Display == Display.Overlay;

            DisplayTimeBox.Text = _settings.Delay.ToString();

            HighlightCheckbox.IsChecked = _settings.HighlightRewards;
            HighContrastCheckbox.IsChecked = _settings.HighContrast;

            // Negate Y for display (positive = up in UI)
            OverlayXBox.Text = _settings.OverlayXOffsetValue.ToString();
            OverlayYBox.Text = (-_settings.OverlayYOffsetValue).ToString();

            MinWidthBox.Text = _settings.MinOverlayWidth.ToString();
            MaxWidthBox.Text = _settings.MaxOverlayWidth.ToString();

            SnapItDelayBox.Text = _settings.SnapItDelay.ToString();
            SnapItCountCheckbox.IsChecked = _settings.DoSnapItCount;
            SnapMultiThreadCheckbox.IsChecked = _settings.SnapMultiThreaded;

            ActivationKeyBtn.Content = _settings.ActivationKey;
            SearchItKeyBtn.Content = _settings.SearchItModifierKey.ToString();
            SnapItKeyBtn.Content = _settings.SnapitModifierKey.ToString();
            MasterItKeyBtn.Content = _settings.MasterItModifierKey.ToString();

            EfficiencyMinBox.Text = _settings.MinimumEfficiencyValue.ToString();
            EfficiencyMaxBox.Text = _settings.MaximumEfficiencyValue.ToString();

            ManualMarketCheckbox.IsChecked = _settings.ManualMarketStatus;

            for (int i = 0; i < ThemeCombo.Items.Count; i++)
            {
                var item = ThemeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is WFtheme theme && theme == _settings.ThemeSelection)
                {
                    ThemeCombo.SelectedIndex = i;
                    break;
                }
            }

            for (int i = 0; i < LocaleCombo.ItemCount; i++)
            {
                var item = LocaleCombo.Items[i] as ComboBoxItem;
                if (item?.Tag?.ToString() == _settings.Locale)
                {
                    LocaleCombo.SelectedIndex = i;
                    break;
                }
            }
            if (LocaleCombo.SelectedIndex < 0)
                LocaleCombo.SelectedIndex = 0;

            ClipboardCheckbox.IsChecked = _settings.Clipboard;
            ClipboardCheckbox.IsEnabled = _settings.Display != Display.Light;

            AutoCheckbox.IsChecked = _settings.Auto;
            AutoListCheckbox.IsChecked = _settings.AutoList;
            AutoCSVCheckbox.IsChecked = _settings.AutoCSV;
            AutoAddCheckbox.IsChecked = _settings.AutoCount;
            AutoListCheckbox.IsEnabled = _settings.Auto;
            AutoCSVCheckbox.IsEnabled = _settings.Auto;
            AutoAddCheckbox.IsEnabled = _settings.Auto;

        }

        private string _socketPath;

        private void UpdateKeyBindingVisibility()
        {
            var inputListener = App.Services?.GetService<WFInfo.Services.IInputListener>();
            bool hasEvdev = inputListener?.StartupWarning == null;
            KeyBindingsSection.IsVisible = hasEvdev;
            SocketInfoSection.IsVisible = !hasEvdev;
            if (!hasEvdev)
            {
                string appImage = Environment.GetEnvironmentVariable("APPIMAGE");
                if (appImage != null)
                {
                    SetupCmdText.Text = $"sudo {appImage} --setup-input";
                }
                else
                {
                    string parentDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/'));
                    string wrapper = parentDir != null ? Path.Combine(parentDir, "WFInfo") : null;
                    string exe = (wrapper != null && File.Exists(wrapper))
                        ? wrapper
                        : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WFInfo.Linux");
                    SetupCmdText.Text = $"sudo {exe} --setup-input";
                }

                string runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
                _socketPath = Path.Combine(runtimeDir, "wfinfo.sock");
            }
        }

        private string GetSelectedSocketCommand()
        {
            if (SocketCmdCombo?.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "activate";
            return "activate";
        }

        private void SocketCmdCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
        }

        private async void CopySetupCmd_Click(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null && SetupCmdText.Text != null)
                await clipboard.SetTextAsync(SetupCmdText.Text);
        }

        private async void CopySocketCmd_Click(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null && _socketPath != null)
            {
                string cmd = GetSelectedSocketCommand();
                await clipboard.SetTextAsync($"echo {cmd} | socat - UNIX-CONNECT:{_socketPath}");
            }
        }



        private void SaveSettings()
        {
            if (OverlayRadio.IsChecked == true)
                _settings.Display = Display.Overlay;
            else if (WindowRadio.IsChecked == true)
                _settings.Display = Display.Window;
            else if (LightRadio.IsChecked == true)
                _settings.Display = Display.Light;

            if (int.TryParse(DisplayTimeBox.Text, out int delay))
                _settings.Delay = delay;

            _settings.HighlightRewards = HighlightCheckbox.IsChecked == true;
            _settings.HighContrast = HighContrastCheckbox.IsChecked == true;

            if (int.TryParse(OverlayXBox.Text, out int ox))
            {
                int halfW = GetGameHalfWidth();
                _settings.OverlayXOffsetValue = Math.Clamp(ox, -halfW, halfW);
            }
            if (int.TryParse(OverlayYBox.Text, out int oy))
            {
                int halfH = GetGameHalfHeight();
                int clamped = Math.Clamp(oy, -halfH, halfH);
                _settings.OverlayYOffsetValue = -clamped; // Negate for internal storage (positive = down internally)
            }

            if (int.TryParse(MinWidthBox.Text, out int minW))
                _settings.MinOverlayWidth = minW;
            if (int.TryParse(MaxWidthBox.Text, out int maxW))
                _settings.MaxOverlayWidth = maxW;

            if (int.TryParse(SnapItDelayBox.Text, out int snapDelay))
                _settings.SnapItDelay = snapDelay;
            _settings.DoSnapItCount = SnapItCountCheckbox.IsChecked == true;
            _settings.SnapMultiThreaded = SnapMultiThreadCheckbox.IsChecked == true;

            if (double.TryParse(EfficiencyMinBox.Text, out double effMin))
                _settings.MinimumEfficiencyValue = effMin;
            if (double.TryParse(EfficiencyMaxBox.Text, out double effMax))
                _settings.MaximumEfficiencyValue = effMax;
            if (_settings.MinimumEfficiencyValue > _settings.MaximumEfficiencyValue)
            {
                // Swap to enforce min <= max
                (_settings.MinimumEfficiencyValue, _settings.MaximumEfficiencyValue) =
                    (_settings.MaximumEfficiencyValue, _settings.MinimumEfficiencyValue);
                EfficiencyMinBox.Text = _settings.MinimumEfficiencyValue.ToString();
                EfficiencyMaxBox.Text = _settings.MaximumEfficiencyValue.ToString();
            }

            // Theme (handled in OnThemeChanged)
            // Locale (handled in OnLocaleChanged)

            _settings.Clipboard = ClipboardCheckbox.IsChecked == true;

            _settings.AutoList = AutoListCheckbox.IsChecked == true;
            _settings.AutoCSV = AutoCSVCheckbox.IsChecked == true;
            _settings.AutoCount = AutoAddCheckbox.IsChecked == true;

            _settings.Save();


        }

        private int GetGameHalfWidth()
        {
            try
            {
                var windowSvc = App.Services.GetRequiredService<IWindowInfoService>();
                return windowSvc.Window.Width > 0 ? windowSvc.Window.Width / 2 : 1000;
            }
            catch { return 1000; }
        }

        private int GetGameHalfHeight()
        {
            try
            {
                var windowSvc = App.Services.GetRequiredService<IWindowInfoService>();
                return windowSvc.Window.Height > 0 ? windowSvc.Window.Height / 2 : 1000;
            }
            catch { return 1000; }
        }

        private void OnCheckChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SaveSettings();
        }

        private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SaveSettings();
        }

        private void OverlayChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            OverlayOffsetSection.IsVisible = true;
            ClipboardCheckbox.IsEnabled = true;
            App.HideNativeOverlays();
            SaveSettings();
        }

        private void WindowChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            OverlayOffsetSection.IsVisible = false;
            ClipboardCheckbox.IsEnabled = true;
            App.HideNativeOverlays();
            SaveSettings();
        }

        private void LightChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            OverlayOffsetSection.IsVisible = false;
            _settings.Clipboard = true;
            ClipboardCheckbox.IsChecked = true;
            ClipboardCheckbox.IsEnabled = false;
            App.HideNativeOverlays();
            SaveSettings();
        }

        private async void OnAutoChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            bool autoChecked = AutoCheckbox.IsChecked == true;
            bool wasAuto = _settings.Auto;

            if (autoChecked && !wasAuto)
            {
                var confirmed = await ShowAutoConfirmDialog();
                if (confirmed)
                {
                    _settings.Auto = true;
                    AppMain.dataBase?.EnableLogCapture();
                    AppMain.AddLog("Auto mode enabled");
                }
                else
                {
                    _loading = true;
                    AutoCheckbox.IsChecked = false;
                    _loading = false;
                    return;
                }
            }
            else if (!autoChecked && wasAuto)
            {
                _settings.Auto = false;
                AppMain.dataBase?.DisableLogCapture();
                AppMain.AddLog("Auto mode disabled");
            }

            AutoListCheckbox.IsEnabled = _settings.Auto;
            AutoCSVCheckbox.IsEnabled = _settings.Auto;
            AutoAddCheckbox.IsEnabled = _settings.Auto;

            SaveSettings();
        }

        private async Task<bool> ShowAutoConfirmDialog()
        {
            var dialog = new Window
            {
                Title = "Automation Mode Opt-In",
                Width = 500, Height = 280,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Black,
                CanResize = false,
                SystemDecorations = SystemDecorations.BorderOnly
            };

            bool result = false;
            var text = new TextBlock
            {
                Text = "Do you want to enable the new auto mode?\n\n" +
                       "This connects to the warframe debug logger to detect the reward window.\n" +
                       "The logger contains info about your pc specs, your public IP, and your email.\n" +
                       "We will be ignoring all of that and only looking for the Fissure Reward Screen.\n" +
                       "We will begin listening after your approval, and it is completely inactive currently.\n\n" +
                       "If you want more information or have questions, please contact us on Discord.",
                Foreground = Avalonia.Media.Brushes.White,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(15, 15, 15, 10),
                FontSize = 13
            };
            var yesBtn = new Button { Content = "Yes", Width = 80, Margin = new Avalonia.Thickness(0, 0, 10, 0) };
            var noBtn = new Button { Content = "No", Width = 80 };
            yesBtn.Click += (_, _) => { result = true; dialog.Close(); };
            noBtn.Click += (_, _) => { result = false; dialog.Close(); };

            var btnPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Children = { yesBtn, noBtn }
            };
            var stack = new StackPanel { Children = { text, btnPanel } };
            dialog.Content = stack;

            await dialog.ShowDialog(this);
            return result;
        }

        private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (ThemeCombo.SelectedItem is not ComboBoxItem themeItem || themeItem.Tag is not WFtheme newTheme)
                return;

            WFtheme oldTheme = _settings.ThemeSelection;

            if (oldTheme == WFtheme.AUTO && newTheme != WFtheme.AUTO)
            {
                var confirmed = await ShowThemeConfirmDialog(newTheme);
                if (!confirmed)
                {
                    _loading = true;
                    for (int i = 0; i < ThemeCombo.Items.Count; i++)
                    {
                        if (ThemeCombo.Items[i] is ComboBoxItem item && item.Tag is WFtheme t && t == oldTheme)
                        {
                            ThemeCombo.SelectedIndex = i;
                            break;
                        }
                    }
                    _loading = false;
                    return;
                }
            }

            _settings.ThemeSelection = newTheme;
            SaveSettings();
        }

        private async Task<bool> ShowThemeConfirmDialog(WFtheme theme)
        {
            var dialog = new Window
            {
                Title = "Theme Change Warning",
                Width = 420, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Black,
                CanResize = false,
                SystemDecorations = SystemDecorations.BorderOnly
            };

            bool result = false;
            var text = new TextBlock
            {
                Text = $"You are about to force WFInfo to think you're using the {theme} theme.\n\n" +
                       "If the theme is wrong, OCR accuracy will be severely impacted.\n" +
                       "Use AUTO unless you have a specific reason to override.",
                Foreground = Avalonia.Media.Brushes.White,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(15, 15, 15, 10),
                FontSize = 13
            };
            var yesBtn = new Button { Content = "Yes", Width = 80, Margin = new Avalonia.Thickness(0, 0, 10, 0) };
            var noBtn = new Button { Content = "No", Width = 80 };
            yesBtn.Click += (_, _) => { result = true; dialog.Close(); };
            noBtn.Click += (_, _) => { result = false; dialog.Close(); };

            var btnPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Children = { yesBtn, noBtn }
            };
            dialog.Content = new StackPanel { Children = { text, btnPanel } };

            await dialog.ShowDialog(this);
            return result;
        }

        private void OnManualMarketChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.ManualMarketStatus = ManualMarketCheckbox.IsChecked == true;
            SaveSettings();
        }

        private void OnLocaleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var selectedLocale = (LocaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(selectedLocale) || selectedLocale == _settings.Locale)
                return;

            _settings.Locale = selectedLocale;
            SaveSettings();

            _ = OCR.updateEngineAsync();
            Task.Run(async () =>
            {
                try
                {
                    await AppMain.dataBase.ReloadItems();
                    AppMain.AddLog($"Locale changed to {selectedLocale}, data reloaded");
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"Locale change reload failed: {ex.Message}");
                    AppMain.StatusUpdate("Locale change failed", 2);
                }
            });
        }

        // --- Key capture for all key bindings ---

        private void ActivationKey_Click(object sender, RoutedEventArgs e)
        {
            StartKeyCapture(KeyCaptureTarget.Activation, ActivationKeyBtn,
                "Press any key to set activation key, or Escape to cancel");
        }

        private void SearchItKey_Click(object sender, RoutedEventArgs e)
        {
            StartKeyCapture(KeyCaptureTarget.SearchIt, SearchItKeyBtn,
                "Press a modifier key for Search It, or Escape to cancel");
        }

        private void SnapItKey_Click(object sender, RoutedEventArgs e)
        {
            StartKeyCapture(KeyCaptureTarget.SnapIt, SnapItKeyBtn,
                "Press a modifier key for Snap It, or Escape to cancel");
        }

        private void MasterItKey_Click(object sender, RoutedEventArgs e)
        {
            StartKeyCapture(KeyCaptureTarget.MasterIt, MasterItKeyBtn,
                "Press a modifier key for Master It, or Escape to cancel");
        }

        private void StartKeyCapture(KeyCaptureTarget target, Button button, string hint)
        {
            _captureTarget = target;
            button.Content = "Press a key...";

        }

        private void OnActivationPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (_captureTarget != KeyCaptureTarget.Activation) return;

            var point = e.GetCurrentPoint(this);
            string mouseBtn = null;
            if (point.Properties.IsMiddleButtonPressed)
                mouseBtn = "Middle";
            else if (point.Properties.IsXButton1Pressed)
                mouseBtn = "XButton1";
            else if (point.Properties.IsXButton2Pressed)
                mouseBtn = "XButton2";

            if (mouseBtn != null)
            {
                e.Handled = true;
                _settings.ActivationKey = mouseBtn;
                ActivationKeyBtn.Content = mouseBtn;
                _captureTarget = KeyCaptureTarget.None;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_captureTarget == KeyCaptureTarget.None) return;

            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                CancelCapture();
                return;
            }

            // In Avalonia, Alt+key reports the actual key in e.Key,
            // but check for LeftAlt/RightAlt directly
            Key actualKey = e.Key;
            if (actualKey == Key.None && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                // Fallback: if key is None with Alt held, treat Alt itself as the key
                actualKey = Key.LeftAlt;
            }

            string keyName = MapAvaloniaKey(actualKey);
            VirtualKey? vk = MapToVirtualKey(actualKey);
            AppMain.AddLog($"KeyCapture: key={actualKey}, mapped={keyName}, vk={vk?.ToString() ?? "NULL"}, target={_captureTarget}");

            switch (_captureTarget)
            {
                case KeyCaptureTarget.Activation:
                    // Check if key conflicts with any modifier
                    if (vk.HasValue && (vk.Value == _settings.SearchItModifierKey ||
                                        vk.Value == _settings.SnapitModifierKey ||
                                        vk.Value == _settings.MasterItModifierKey))
                    {
                        CancelCapture();
                        return;
                    }
                    _settings.ActivationKey = keyName;
                    ActivationKeyBtn.Content = keyName;
                    break;
                case KeyCaptureTarget.SearchIt:
                    if (vk.HasValue && (vk.Value == _settings.SnapitModifierKey ||
                                        vk.Value == _settings.MasterItModifierKey))
                    {
                        CancelCapture();
                        return;
                    }
                    if (vk.HasValue) _settings.SearchItModifierKey = vk.Value;
                    SearchItKeyBtn.Content = keyName;
                    break;
                case KeyCaptureTarget.SnapIt:
                    if (vk.HasValue && (vk.Value == _settings.SearchItModifierKey ||
                                        vk.Value == _settings.MasterItModifierKey))
                    {
                        CancelCapture();
                        return;
                    }
                    if (vk.HasValue) _settings.SnapitModifierKey = vk.Value;
                    SnapItKeyBtn.Content = keyName;
                    break;
                case KeyCaptureTarget.MasterIt:
                    if (vk.HasValue && (vk.Value == _settings.SearchItModifierKey ||
                                        vk.Value == _settings.SnapitModifierKey))
                    {
                        CancelCapture();
                        return;
                    }
                    if (vk.HasValue) _settings.MasterItModifierKey = vk.Value;
                    MasterItKeyBtn.Content = keyName;
                    break;

            }

            _captureTarget = KeyCaptureTarget.None;
        }

        private void CancelCapture()
        {
            switch (_captureTarget)
            {
                case KeyCaptureTarget.Activation:
                    ActivationKeyBtn.Content = _settings.ActivationKey;
                    break;
                case KeyCaptureTarget.SearchIt:
                    SearchItKeyBtn.Content = _settings.SearchItModifierKey.ToString();
                    break;
                case KeyCaptureTarget.SnapIt:
                    SnapItKeyBtn.Content = _settings.SnapitModifierKey.ToString();
                    break;
                case KeyCaptureTarget.MasterIt:
                    MasterItKeyBtn.Content = _settings.MasterItModifierKey.ToString();
                    break;

            }
            _captureTarget = KeyCaptureTarget.None;
        }

        private static string MapAvaloniaKey(Key key)
        {
            return key switch
            {
                Key.PrintScreen => "Snapshot",
                Key.Pause => "Pause",
                _ => key.ToString()
            };
        }

        private static VirtualKey? MapToVirtualKey(Key key)
        {
            return key switch
            {
                Key.LeftShift => VirtualKey.LeftShift,
                Key.RightShift => VirtualKey.RightShift,
                Key.LeftCtrl => VirtualKey.LeftCtrl,
                Key.RightCtrl => VirtualKey.RightCtrl,
                Key.LeftAlt => VirtualKey.LeftAlt,
                Key.RightAlt => VirtualKey.RightAlt,
                Key.PrintScreen => VirtualKey.PrintScreen,
                Key.Space => VirtualKey.Space,
                Key.Enter => VirtualKey.Enter,
                Key.Escape => VirtualKey.None,
                Key.Tab => VirtualKey.Tab,
                Key.Back => VirtualKey.Back,
                Key.Delete => VirtualKey.Delete,
                Key.A => VirtualKey.A, Key.B => VirtualKey.B, Key.C => VirtualKey.C,
                Key.D => VirtualKey.D, Key.E => VirtualKey.E, Key.F => VirtualKey.F,
                Key.G => VirtualKey.G, Key.H => VirtualKey.H, Key.I => VirtualKey.I,
                Key.J => VirtualKey.J, Key.K => VirtualKey.K, Key.L => VirtualKey.L,
                Key.M => VirtualKey.M, Key.N => VirtualKey.N, Key.O => VirtualKey.O,
                Key.P => VirtualKey.P, Key.Q => VirtualKey.Q, Key.R => VirtualKey.R,
                Key.S => VirtualKey.S, Key.T => VirtualKey.T, Key.U => VirtualKey.U,
                Key.V => VirtualKey.V, Key.W => VirtualKey.W, Key.X => VirtualKey.X,
                Key.Y => VirtualKey.Y, Key.Z => VirtualKey.Z,
                Key.D0 => VirtualKey.D0, Key.D1 => VirtualKey.D1, Key.D2 => VirtualKey.D2,
                Key.D3 => VirtualKey.D3, Key.D4 => VirtualKey.D4, Key.D5 => VirtualKey.D5,
                Key.D6 => VirtualKey.D6, Key.D7 => VirtualKey.D7, Key.D8 => VirtualKey.D8,
                Key.D9 => VirtualKey.D9,
                Key.F1 => VirtualKey.F1, Key.F2 => VirtualKey.F2, Key.F3 => VirtualKey.F3,
                Key.F4 => VirtualKey.F4, Key.F5 => VirtualKey.F5, Key.F6 => VirtualKey.F6,
                Key.F7 => VirtualKey.F7, Key.F8 => VirtualKey.F8, Key.F9 => VirtualKey.F9,
                Key.F10 => VirtualKey.F10, Key.F11 => VirtualKey.F11, Key.F12 => VirtualKey.F12,
                Key.OemTilde => VirtualKey.OemTilde, Key.OemMinus => VirtualKey.OemMinus,
                Key.OemPlus => VirtualKey.OemPlus, Key.OemOpenBrackets => VirtualKey.OemOpenBrackets,
                Key.OemCloseBrackets => VirtualKey.OemCloseBrackets, Key.OemPipe => VirtualKey.OemPipe,
                Key.OemSemicolon => VirtualKey.OemSemicolon, Key.OemQuotes => VirtualKey.OemQuotes,
                Key.OemComma => VirtualKey.OemComma, Key.OemPeriod => VirtualKey.OemPeriod,
                Key.OemQuestion => VirtualKey.OemSlash, Key.OemBackslash => VirtualKey.OemBackslash,
                _ => null
            };
        }

        private void ConfigureTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeAdjusterWindow.ShowThemeAdjuster();
        }

        private void CreateDebugZip_Click(object sender, RoutedEventArgs e)
        {
            AppMain.SpawnErrorPopup(DateTime.UtcNow, 1800);
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Avalonia.Visual)sender);
                if (pos.Y > 22) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void ResizeGrip_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                try { BeginResizeDrag(WindowEdge.South, e); } catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Hide();
        }
    }
}

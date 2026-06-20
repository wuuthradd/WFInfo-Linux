using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using WFInfo.Settings;
using WFInfo.Services;

namespace WFInfo.Linux.Views
{
    public partial class ThemeAdjusterWindow : Window
    {
        public static ThemeAdjusterWindow INSTANCE;

        private SKBitmap _unfiltered;
        private readonly ApplicationSettings _settings;
        private bool _loading = true;
        private static readonly string _filtersDir = Path.Combine(PlatformPaths.AppDataPath, "filters");

        public ThemeAdjusterWindow()
        {
            InitializeComponent();
            INSTANCE = this;
            _settings = ApplicationSettings.GlobalSettings;
            LoadSettingsToUI();
            SyncAllTextBoxes();
            RefreshFilterDropdown();
            _loading = false;
        }

        public static void ShowThemeAdjuster()
        {
            if (INSTANCE == null)
                new ThemeAdjusterWindow();
            INSTANCE.Show();
            INSTANCE.Activate();
        }

        private void LoadSettingsToUI()
        {
            _loading = true;

            PrimaryHslFilter.IsFilterEnabled = _settings.CF_usePrimaryHSL;
            PrimaryHslFilter.Value1Max = _settings.CF_pHueMax;
            PrimaryHslFilter.Value1Min = _settings.CF_pHueMin;
            PrimaryHslFilter.Value2Max = _settings.CF_pSatMax;
            PrimaryHslFilter.Value2Min = _settings.CF_pSatMin;
            PrimaryHslFilter.Value3Max = _settings.CF_pBrightMax;
            PrimaryHslFilter.Value3Min = _settings.CF_pBrightMin;

            PrimaryRgbFilter.IsFilterEnabled = _settings.CF_usePrimaryRGB;
            PrimaryRgbFilter.Value1Max = _settings.CF_pRMax;
            PrimaryRgbFilter.Value1Min = _settings.CF_pRMin;
            PrimaryRgbFilter.Value2Max = _settings.CF_pGMax;
            PrimaryRgbFilter.Value2Min = _settings.CF_pGMin;
            PrimaryRgbFilter.Value3Max = _settings.CF_pBMax;
            PrimaryRgbFilter.Value3Min = _settings.CF_pBMin;

            SecondaryHslFilter.IsFilterEnabled = _settings.CF_useSecondaryHSL;
            SecondaryHslFilter.Value1Max = _settings.CF_sHueMax;
            SecondaryHslFilter.Value1Min = _settings.CF_sHueMin;
            SecondaryHslFilter.Value2Max = _settings.CF_sSatMax;
            SecondaryHslFilter.Value2Min = _settings.CF_sSatMin;
            SecondaryHslFilter.Value3Max = _settings.CF_sBrightMax;
            SecondaryHslFilter.Value3Min = _settings.CF_sBrightMin;

            SecondaryRgbFilter.IsFilterEnabled = _settings.CF_useSecondaryRGB;
            SecondaryRgbFilter.Value1Max = _settings.CF_sRMax;
            SecondaryRgbFilter.Value1Min = _settings.CF_sRMin;
            SecondaryRgbFilter.Value2Max = _settings.CF_sGMax;
            SecondaryRgbFilter.Value2Min = _settings.CF_sGMin;
            SecondaryRgbFilter.Value3Max = _settings.CF_sBMax;
            SecondaryRgbFilter.Value3Min = _settings.CF_sBMin;

            _loading = false;
        }

        private void SyncAllTextBoxes()
        {
            PrimaryHslFilter.SyncTextBoxes();
            PrimaryRgbFilter.SyncTextBoxes();
            SecondaryHslFilter.SyncTextBoxes();
            SecondaryRgbFilter.SyncTextBoxes();
        }

        private void SaveUIToSettings()
        {
            if (_loading) return;

            _settings.CF_usePrimaryHSL = PrimaryHslFilter.IsFilterEnabled;
            _settings.CF_pHueMax = (float)PrimaryHslFilter.Value1Max;
            _settings.CF_pHueMin = (float)PrimaryHslFilter.Value1Min;
            _settings.CF_pSatMax = (float)PrimaryHslFilter.Value2Max;
            _settings.CF_pSatMin = (float)PrimaryHslFilter.Value2Min;
            _settings.CF_pBrightMax = (float)PrimaryHslFilter.Value3Max;
            _settings.CF_pBrightMin = (float)PrimaryHslFilter.Value3Min;

            _settings.CF_usePrimaryRGB = PrimaryRgbFilter.IsFilterEnabled;
            _settings.CF_pRMax = (int)PrimaryRgbFilter.Value1Max;
            _settings.CF_pRMin = (int)PrimaryRgbFilter.Value1Min;
            _settings.CF_pGMax = (int)PrimaryRgbFilter.Value2Max;
            _settings.CF_pGMin = (int)PrimaryRgbFilter.Value2Min;
            _settings.CF_pBMax = (int)PrimaryRgbFilter.Value3Max;
            _settings.CF_pBMin = (int)PrimaryRgbFilter.Value3Min;

            _settings.CF_useSecondaryHSL = SecondaryHslFilter.IsFilterEnabled;
            _settings.CF_sHueMax = (float)SecondaryHslFilter.Value1Max;
            _settings.CF_sHueMin = (float)SecondaryHslFilter.Value1Min;
            _settings.CF_sSatMax = (float)SecondaryHslFilter.Value2Max;
            _settings.CF_sSatMin = (float)SecondaryHslFilter.Value2Min;
            _settings.CF_sBrightMax = (float)SecondaryHslFilter.Value3Max;
            _settings.CF_sBrightMin = (float)SecondaryHslFilter.Value3Min;

            _settings.CF_useSecondaryRGB = SecondaryRgbFilter.IsFilterEnabled;
            _settings.CF_sRMax = (int)SecondaryRgbFilter.Value1Max;
            _settings.CF_sRMin = (int)SecondaryRgbFilter.Value1Min;
            _settings.CF_sGMax = (int)SecondaryRgbFilter.Value2Max;
            _settings.CF_sGMin = (int)SecondaryRgbFilter.Value2Min;
            _settings.CF_sBMax = (int)SecondaryRgbFilter.Value3Max;
            _settings.CF_sBMin = (int)SecondaryRgbFilter.Value3Min;
        }

        private void OnFilterValuesChanged(object sender, EventArgs e)
        {
            SaveUIToSettings();
        }

        private void OnFilterEnabledChanged(object sender, EventArgs e)
        {
            SaveUIToSettings();
            _settings.Save();
        }

        private void ApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_unfiltered == null) return;
            SaveUIToSettings();

            var snapshot = _unfiltered;
            Task.Run(() =>
            {
                var filtered = OCR.ScaleUpAndFilter(snapshot, WFtheme.CUSTOM, out _, out _);
                Dispatcher.UIThread.Post(() =>
                {
                    SetPreviewImage(filtered);
                    filtered.Dispose();
                });
            });
        }

        private void ShowUnfiltered_Click(object sender, RoutedEventArgs e)
        {
            if (_unfiltered != null)
                SetPreviewImage(_unfiltered);
        }

        private void LoadLatest_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    string debugDir = Path.Combine(AppMain.AppPath, "debug");
                    if (!Directory.Exists(debugDir)) { AppMain.StatusUpdate("No Debug directory found", 1); return; }

                    var files = new DirectoryInfo(debugDir).GetFiles("*FullScreenShot*")
                        .OrderByDescending(f => f.CreationTimeUtc).ToList();

                    if (files.Count == 0) { AppMain.StatusUpdate("No screenshots found", 1); return; }

                    AppMain.AddLog("ThemeAdjuster: Loading " + files[0].Name);
                    using var stream = File.OpenRead(files[0].FullName);
                    var image = SKBitmap.Decode(stream);
                    if (image != null)
                    {
                        _unfiltered?.Dispose();
                        _unfiltered = image;
                        var img = image;
                        Dispatcher.UIThread.Post(() => SetPreviewImage(img));
                    }
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"ThemeAdjuster: Load failed: {ex.Message}");
                    AppMain.StatusUpdate("Failed to load image", 1);
                }
            });
        }

        private async void LoadFromFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var imageFilter = new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.bmp" } };
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { Title = "Load Screenshot", FileTypeFilter = new[] { imageFilter, FilePickerFileTypes.All } });

                if (files == null || files.Count == 0) return;

                var path = files[0].Path?.LocalPath;
                if (path == null) return;

                await Task.Run(() =>
                {
                    AppMain.AddLog("ThemeAdjuster: Loading " + path);
                    using var stream = File.OpenRead(path);
                    var image = SKBitmap.Decode(stream);
                    if (image != null)
                    {
                        _unfiltered?.Dispose();
                        _unfiltered = image;
                        var img = image;
                        Dispatcher.UIThread.Post(() => SetPreviewImage(img));
                    }
                });
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Load failed: {ex.Message}");
                AppMain.StatusUpdate("Failed to load image", 1);
            }
        }

        private JObject BuildFilterJson()
        {
            SaveUIToSettings();
            return new JObject
            {
                { "CF_usePrimaryHSL", _settings.CF_usePrimaryHSL },
                { "CF_pHueMax", _settings.CF_pHueMax }, { "CF_pHueMin", _settings.CF_pHueMin },
                { "CF_pSatMax", _settings.CF_pSatMax }, { "CF_pSatMin", _settings.CF_pSatMin },
                { "CF_pBrightMax", _settings.CF_pBrightMax }, { "CF_pBrightMin", _settings.CF_pBrightMin },
                { "CF_usePrimaryRGB", _settings.CF_usePrimaryRGB },
                { "CF_pRMax", _settings.CF_pRMax }, { "CF_pRMin", _settings.CF_pRMin },
                { "CF_pGMax", _settings.CF_pGMax }, { "CF_pGMin", _settings.CF_pGMin },
                { "CF_pBMax", _settings.CF_pBMax }, { "CF_pBMin", _settings.CF_pBMin },
                { "CF_useSecondaryHSL", _settings.CF_useSecondaryHSL },
                { "CF_sHueMax", _settings.CF_sHueMax }, { "CF_sHueMin", _settings.CF_sHueMin },
                { "CF_sSatMax", _settings.CF_sSatMax }, { "CF_sSatMin", _settings.CF_sSatMin },
                { "CF_sBrightMax", _settings.CF_sBrightMax }, { "CF_sBrightMin", _settings.CF_sBrightMin },
                { "CF_useSecondaryRGB", _settings.CF_useSecondaryRGB },
                { "CF_sRMax", _settings.CF_sRMax }, { "CF_sRMin", _settings.CF_sRMin },
                { "CF_sGMax", _settings.CF_sGMax }, { "CF_sGMin", _settings.CF_sGMin },
                { "CF_sBMax", _settings.CF_sBMax }, { "CF_sBMin", _settings.CF_sBMin }
            };
        }

        // Validates all CF_* fields then applies them to settings. Throws on invalid JSON.
        private void ApplyFilterJson(JObject json)
        {
            bool CF_usePrimaryHSL = json["CF_usePrimaryHSL"].ToObject<bool>();
            float CF_pHueMax = json["CF_pHueMax"].ToObject<float>();
            float CF_pHueMin = json["CF_pHueMin"].ToObject<float>();
            float CF_pSatMax = json["CF_pSatMax"].ToObject<float>();
            float CF_pSatMin = json["CF_pSatMin"].ToObject<float>();
            float CF_pBrightMax = json["CF_pBrightMax"].ToObject<float>();
            float CF_pBrightMin = json["CF_pBrightMin"].ToObject<float>();

            bool CF_usePrimaryRGB = json["CF_usePrimaryRGB"].ToObject<bool>();
            int CF_pRMax = json["CF_pRMax"].ToObject<int>();
            int CF_pRMin = json["CF_pRMin"].ToObject<int>();
            int CF_pGMax = json["CF_pGMax"].ToObject<int>();
            int CF_pGMin = json["CF_pGMin"].ToObject<int>();
            int CF_pBMax = json["CF_pBMax"].ToObject<int>();
            int CF_pBMin = json["CF_pBMin"].ToObject<int>();

            bool CF_useSecondaryHSL = json["CF_useSecondaryHSL"].ToObject<bool>();
            float CF_sHueMax = json["CF_sHueMax"].ToObject<float>();
            float CF_sHueMin = json["CF_sHueMin"].ToObject<float>();
            float CF_sSatMax = json["CF_sSatMax"].ToObject<float>();
            float CF_sSatMin = json["CF_sSatMin"].ToObject<float>();
            float CF_sBrightMax = json["CF_sBrightMax"].ToObject<float>();
            float CF_sBrightMin = json["CF_sBrightMin"].ToObject<float>();

            bool CF_useSecondaryRGB = json["CF_useSecondaryRGB"].ToObject<bool>();
            int CF_sRMax = json["CF_sRMax"].ToObject<int>();
            int CF_sRMin = json["CF_sRMin"].ToObject<int>();
            int CF_sGMax = json["CF_sGMax"].ToObject<int>();
            int CF_sGMin = json["CF_sGMin"].ToObject<int>();
            int CF_sBMax = json["CF_sBMax"].ToObject<int>();
            int CF_sBMin = json["CF_sBMin"].ToObject<int>();

            _settings.CF_usePrimaryHSL = CF_usePrimaryHSL;
            _settings.CF_pHueMax = CF_pHueMax;
            _settings.CF_pHueMin = CF_pHueMin;
            _settings.CF_pSatMax = CF_pSatMax;
            _settings.CF_pSatMin = CF_pSatMin;
            _settings.CF_pBrightMax = CF_pBrightMax;
            _settings.CF_pBrightMin = CF_pBrightMin;

            _settings.CF_usePrimaryRGB = CF_usePrimaryRGB;
            _settings.CF_pRMax = CF_pRMax;
            _settings.CF_pRMin = CF_pRMin;
            _settings.CF_pGMax = CF_pGMax;
            _settings.CF_pGMin = CF_pGMin;
            _settings.CF_pBMax = CF_pBMax;
            _settings.CF_pBMin = CF_pBMin;

            _settings.CF_useSecondaryHSL = CF_useSecondaryHSL;
            _settings.CF_sHueMax = CF_sHueMax;
            _settings.CF_sHueMin = CF_sHueMin;
            _settings.CF_sSatMax = CF_sSatMax;
            _settings.CF_sSatMin = CF_sSatMin;
            _settings.CF_sBrightMax = CF_sBrightMax;
            _settings.CF_sBrightMin = CF_sBrightMin;

            _settings.CF_useSecondaryRGB = CF_useSecondaryRGB;
            _settings.CF_sRMax = CF_sRMax;
            _settings.CF_sRMin = CF_sRMin;
            _settings.CF_sGMax = CF_sGMax;
            _settings.CF_sGMin = CF_sGMin;
            _settings.CF_sBMax = CF_sBMax;
            _settings.CF_sBMin = CF_sBMin;

            _settings.Save();
            LoadSettingsToUI();
            SyncAllTextBoxes();
        }

        private void ExportFilter_Click(object sender, RoutedEventArgs e)
        {
            FilterTextBox.Text = JsonConvert.SerializeObject(BuildFilterJson(), Formatting.None);
        }

        private void ImportFilter_Click(object sender, RoutedEventArgs e)
        {
            string input = FilterTextBox.Text;
            if (string.IsNullOrWhiteSpace(input)) return;

            try
            {
                JObject json = JsonConvert.DeserializeObject<JObject>(input);
                ApplyFilterJson(json);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Custom Filter Import failed. Input: " + Environment.NewLine + input + Environment.NewLine + "Custom filter import error message: " + ex.Message);
                AppMain.StatusUpdate("Invalid filter data", 1);
            }
        }

        private async void ExportJsonFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var jsonType = new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } };
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Export Filter",
                        DefaultExtension = "json",
                        FileTypeChoices = new[] { jsonType }
                    });

                if (file == null) return;
                var path = file.Path?.LocalPath;
                if (path == null) return;

                var json = BuildFilterJson();
                await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(json, Formatting.Indented));
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Export JSON failed: {ex.Message}");
                AppMain.StatusUpdate("Failed to export filter", 1);
            }
        }

        private async void ImportJsonFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var jsonType = new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } };
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Import Filter",
                        FileTypeFilter = new[] { jsonType, FilePickerFileTypes.All }
                    });

                if (files == null || files.Count == 0) return;
                var path = files[0].Path?.LocalPath;
                if (path == null) return;

                string content = await File.ReadAllTextAsync(path);
                JObject json = JsonConvert.DeserializeObject<JObject>(content);
                ApplyFilterJson(json);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Import JSON failed: {ex.Message}");
                AppMain.StatusUpdate("Invalid filter file", 1);
            }
        }

        private const string SaveAsNewItem = "Save as New";

        private void RefreshFilterDropdown()
        {
            FilterDropdown.SelectionChanged -= FilterDropdown_SelectionChanged;
            FilterDropdown.Items.Clear();
            FilterDropdown.Items.Add(SaveAsNewItem);

            if (Directory.Exists(_filtersDir))
            {
                foreach (var file in Directory.GetFiles(_filtersDir, "*.json").OrderBy(f => f))
                    FilterDropdown.Items.Add(Path.GetFileNameWithoutExtension(file));
            }

            FilterDropdown.SelectedIndex = -1;
            FilterDropdown.SelectionChanged += FilterDropdown_SelectionChanged;
        }

        private async void FilterDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilterDropdown.SelectedItem is not string name) return;

            if (name == SaveAsNewItem)
            {
                // Reset to placeholder so "Save as New" can be selected again next time
                FilterDropdown.SelectionChanged -= FilterDropdown_SelectionChanged;
                FilterDropdown.SelectedIndex = -1;
                FilterDropdown.SelectionChanged += FilterDropdown_SelectionChanged;

                var result = await ShowFilterNameDialog();
                if (result == null) return;

                Directory.CreateDirectory(_filtersDir);
                var json = BuildFilterJson();
                await File.WriteAllTextAsync(Path.Combine(_filtersDir, result + ".json"),
                    JsonConvert.SerializeObject(json, Formatting.Indented));

                RefreshFilterDropdown();
                for (int i = 0; i < FilterDropdown.Items.Count; i++)
                {
                    if ((string)FilterDropdown.Items[i] == result)
                    {
                        FilterDropdown.SelectedIndex = i;
                        break;
                    }
                }
                return;
            }

            string path = Path.Combine(_filtersDir, name + ".json");
            try
            {
                string content = File.ReadAllText(path);
                JObject json = JsonConvert.DeserializeObject<JObject>(content);
                ApplyFilterJson(json);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Failed to load filter '{name}': {ex.Message}");
                AppMain.StatusUpdate("Failed to load filter", 1);
            }
        }

        private async void SaveFilter_Click(object sender, RoutedEventArgs e)
        {
            string selected = FilterDropdown.SelectedItem as string;

            // Existing filter selected, confirm then overwrite
            if (selected != null && selected != SaveAsNewItem)
            {
                if (!await ShowConfirmDialog($"Overwrite \"{selected}\"?"))
                    return;

                Directory.CreateDirectory(_filtersDir);
                var json = BuildFilterJson();
                await File.WriteAllTextAsync(Path.Combine(_filtersDir, selected + ".json"),
                    JsonConvert.SerializeObject(json, Formatting.Indented));
                return;
            }

            // "Save as New" or placeholder state, show name dialog
            var result = await ShowFilterNameDialog();
            if (result == null) return;

            Directory.CreateDirectory(_filtersDir);
            var newJson = BuildFilterJson();
            await File.WriteAllTextAsync(Path.Combine(_filtersDir, result + ".json"),
                JsonConvert.SerializeObject(newJson, Formatting.Indented));

            RefreshFilterDropdown();
            for (int i = 0; i < FilterDropdown.Items.Count; i++)
            {
                if ((string)FilterDropdown.Items[i] == result)
                {
                    FilterDropdown.SelectedIndex = i;
                    break;
                }
            }
        }

        private async Task<string> ShowFilterNameDialog()
        {
            var dialog = new Window
            {
                Title = "Save Filter",
                Width = 300, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                CanResize = false,
                SystemDecorations = SystemDecorations.BorderOnly
            };

            string result = null;
            var nameBox = new TextBox { MaxLength = 30, Margin = new Thickness(10, 10, 10, 5), Watermark = "Filter name" };
            var errorText = new TextBlock
            {
                Foreground = Brushes.Red,
                Margin = new Thickness(10, 0, 10, 5),
                FontSize = 12,
                IsVisible = false
            };
            var okBtn = new Button { Content = "Save", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 70 };

            okBtn.Click += (_, _) =>
            {
                string name = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    errorText.Text = "Name cannot be empty";
                    errorText.IsVisible = true;
                    return;
                }

                if (string.Equals(name, SaveAsNewItem, StringComparison.OrdinalIgnoreCase))
                {
                    errorText.Text = "This name is reserved";
                    errorText.IsVisible = true;
                    return;
                }

                char[] invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                {
                    errorText.Text = "Name contains invalid characters";
                    errorText.IsVisible = true;
                    return;
                }

                if (File.Exists(Path.Combine(_filtersDir, name + ".json")))
                {
                    errorText.Text = "A filter with this name already exists";
                    errorText.IsVisible = true;
                    return;
                }

                result = name;
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => dialog.Close();

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                Children = { okBtn, cancelBtn }
            };
            dialog.Content = new StackPanel { Children = { nameBox, errorText, btnPanel } };

            await dialog.ShowDialog(this);
            return result;
        }

        private async Task<bool> ShowConfirmDialog(string message)
        {
            var dialog = new Window
            {
                Title = "Confirm",
                Width = 300, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                CanResize = false,
                SystemDecorations = SystemDecorations.BorderOnly
            };

            bool confirmed = false;
            var text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(15, 15, 15, 10),
                FontSize = 13
            };
            var yesBtn = new Button { Content = "Yes", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var noBtn = new Button { Content = "No", Width = 70 };
            yesBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };
            noBtn.Click += (_, _) => dialog.Close();

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { yesBtn, noBtn }
            };
            dialog.Content = new StackPanel { Children = { text, btnPanel } };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        private async void DeleteFilter_Click(object sender, RoutedEventArgs e)
        {
            if (FilterDropdown.SelectedItem is not string name || name == SaveAsNewItem) return;

            if (!await ShowConfirmDialog($"Delete \"{name}\"?"))
                return;

            string path = Path.Combine(_filtersDir, name + ".json");
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Failed to delete filter '{name}': {ex.Message}");
                AppMain.StatusUpdate("Failed to delete filter", 1);
                return;
            }

            RefreshFilterDropdown();
        }

        private void SetPreviewImage(SKBitmap bitmap)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream(data.ToArray());
            (PreviewImage.Source as IDisposable)?.Dispose();
            PreviewImage.Source = new Bitmap(stream);
        }

        private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                try { BeginMoveDrag(e); } catch (InvalidOperationException) { }
        }

        private void ResizeGrip_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                try { BeginResizeDrag(WindowEdge.SouthEast, e); } catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SaveUIToSettings();
            _settings.Save();
            _unfiltered?.Dispose();
            _unfiltered = null;
            (PreviewImage.Source as IDisposable)?.Dispose();
            PreviewImage.Source = null;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            _unfiltered?.Dispose();
            if (INSTANCE == this) INSTANCE = null;
            base.OnClosed(e);
        }
    }
}
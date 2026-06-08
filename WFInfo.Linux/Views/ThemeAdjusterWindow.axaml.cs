using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class ThemeAdjusterWindow : Window
    {
        public static ThemeAdjusterWindow INSTANCE;

        private SKBitmap _unfiltered;
        private readonly ApplicationSettings _settings;
        private bool _loading = true;

        public ThemeAdjusterWindow()
        {
            InitializeComponent();
            INSTANCE = this;
            _settings = ApplicationSettings.GlobalSettings;
            LoadSettingsToUI();
            SyncAllTextBoxes();
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

            Task.Run(() =>
            {
                var filtered = OCR.ScaleUpAndFilter(_unfiltered, WFtheme.CUSTOM, out _, out _);
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
                    string debugDir = Path.Combine(AppMain.AppPath, "Debug");
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
                        Dispatcher.UIThread.Post(() => SetPreviewImage(_unfiltered));
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
                        Dispatcher.UIThread.Post(() => SetPreviewImage(_unfiltered));
                    }
                });
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ThemeAdjuster: Load failed: {ex.Message}");
                AppMain.StatusUpdate("Failed to load image", 1);
            }
        }

        private void ExportFilter_Click(object sender, RoutedEventArgs e)
        {
            SaveUIToSettings();
            var exp = new JObject
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
            FilterTextBox.Text = JsonConvert.SerializeObject(exp, Formatting.None);
        }

        private void ImportFilter_Click(object sender, RoutedEventArgs e)
        {
            string input = FilterTextBox.Text;
            try
            {
                // Read all parameters to temporary variables first (atomic validation, matches WPF)
                JObject json = JsonConvert.DeserializeObject<JObject>(input);
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
            catch (Exception ex)
            {
                AppMain.AddLog("Custom Filter Import failed. Input: " + Environment.NewLine + input + Environment.NewLine + "Custom filter import error message: " + ex.Message);
                AppMain.SpawnErrorPopup(DateTime.UtcNow);
            }
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
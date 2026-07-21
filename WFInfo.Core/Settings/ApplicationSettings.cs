using System;
using System.IO;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WFInfo.Models;
using WFInfo.Services;

namespace WFInfo.Settings
{
    /// <summary>
    /// Cross-platform application settings with JSON persistence.
    /// </summary>
    public class ApplicationSettings : IReadOnlyApplicationSettings
    {
        private static readonly Lazy<ApplicationSettings> _instance = new Lazy<ApplicationSettings>(() =>
        {
            var settings = new ApplicationSettings();
            settings.Load();
            return settings;
        });

        public static ApplicationSettings GlobalSettings => _instance.Value;
        public static IReadOnlyApplicationSettings GlobalReadonlySettings => GlobalSettings;

        private static string SettingsPath => Path.Combine(PlatformPaths.AppDataPath, "settings.json");

        [JsonIgnore]
        public bool Initialized { get; set; } = false;
        public Display Display { get; set; } = Display.Overlay;
        [JsonIgnore]
        public bool IsLightSelected => Display == Display.Light;
        public string ActivationKey { get; set; } = "PrintScreen";
        [JsonIgnore]
        public VirtualKey? ActivationKeyKey => Enum.TryParse<VirtualKey>(ActivationKey, out var res) ? res : (VirtualKey?)null;
        [JsonIgnore]
        public VirtualMouseButton? ActivationMouseButton
        {
            get
            {
                var result = Enum.TryParse<VirtualMouseButton>(ActivationKey, out var res) ? res : (VirtualMouseButton?)null;
                if (result is VirtualMouseButton.Left || result is VirtualMouseButton.Right)
                    return null;
                return result;
            }
        }
        public VirtualKey DebugModifierKey { get; set; } = VirtualKey.LeftShift;
        public VirtualKey SearchItModifierKey { get; set; } = VirtualKey.OemTilde;
        public VirtualKey SnapitModifierKey { get; set; } = VirtualKey.LeftCtrl;
        public VirtualKey MasterItModifierKey { get; set; } = VirtualKey.RightCtrl;
        public bool Debug { get; set; } = false;
        public string Locale { get; set; } = "en";
        public bool Clipboard { get; set; } = false;
        public long AutoDelay { get; set; } = 500L;
        public int ImageRetentionTime { get; set; } = 12;
        public string ClipboardTemplate { get; set; } = "-- PC 48 hours avg price by WFM (c) WFInfo";
        public bool SnapitExport { get; set; } = false;
        public int Delay { get; set; } = 10000;
        public bool HighlightRewards { get; set; } = true;
        public bool ClipboardVaulted { get; set; } = false;
        public bool Auto { get; set; } = false;
        public bool HighContrast { get; set; } = false;
        public int OverlayXOffsetValue { get; set; } = 0;
        public int OverlayYOffsetValue { get; set; } = 0;
        public bool AutoList { get; set; } = false;
        public bool AutoCSV { get; set; } = false;
        public bool AutoCount { get; set; } = false;
        public double MaximumEfficiencyValue { get; set; } = 9.5;
        public double MinimumEfficiencyValue { get; set; } = 4.5;
        public bool DoSnapItCount { get; set; } = false;
        public int SnapItDelay { get; set; } = 20000;
        public double SnapItHorizontalNameMargin { get; set; } = 0.25;
        public bool DoCustomNumberBoxWidth { get; set; } = false;
        public double SnapItNumberBoxWidth { get; set; } = 0.4;
        public bool SnapMultiThreaded { get; set; } = true;
        public double SnapRowTextDensity { get; set; } = 0.015;
        public double SnapRowEmptyDensity { get; set; } = 0.01;
        public double SnapColEmptyDensity { get; set; } = 0.005;
        public int MinOverlayWidth { get; set; } = 120;
        public int MaxOverlayWidth { get; set; } = 160;

        public WFtheme ThemeSelection { get; set; } = WFtheme.AUTO;
        public bool CF_usePrimaryHSL { get; set; } = false;
        public bool CF_usePrimaryRGB { get; set; } = false;
        public bool CF_useSecondaryHSL { get; set; } = false;
        public bool CF_useSecondaryRGB { get; set; } = false;
        public float CF_pHueMax { get; set; } = 360.0F;
        public float CF_pHueMin { get; set; } = 0.0F;
        public float CF_pSatMax { get; set; } = 1.0F;
        public float CF_pSatMin { get; set; } = 0.0F;
        public float CF_pBrightMax { get; set; } = 1.0F;
        public float CF_pBrightMin { get; set; } = 0.0F;
        public int CF_pRMax { get; set; } = 255;
        public int CF_pRMin { get; set; } = 0;
        public int CF_pGMax { get; set; } = 255;
        public int CF_pGMin { get; set; } = 0;
        public int CF_pBMax { get; set; } = 255;
        public int CF_pBMin { get; set; } = 0;
        public float CF_sHueMax { get; set; } = 360.0F;
        public float CF_sHueMin { get; set; } = 0.0F;
        public float CF_sSatMax { get; set; } = 1.0F;
        public float CF_sSatMin { get; set; } = 0.0F;
        public float CF_sBrightMax { get; set; } = 1.0F;
        public float CF_sBrightMin { get; set; } = 0.0F;
        public int CF_sRMax { get; set; } = 255;
        public int CF_sRMin { get; set; } = 0;
        public int CF_sGMax { get; set; } = 255;
        public int CF_sGMin { get; set; } = 0;
        public int CF_sBMax { get; set; } = 255;
        public int CF_sBMin { get; set; } = 0;
        public long FixedAutoDelay { get; set; } = 500L;
        public string IgnoredUpdate { get; set; } = null;
        public bool ManualMarketStatus { get; set; } = false;
        public string MarketStatus { get; set; } = "ingame";
        public bool WhisperNotifications { get; set; } = true;
        public string WhisperSound { get; set; } = "Time Is Now";
        public bool AutoTradeDone { get; set; } = false;

        public bool TradeDecrementInventory { get; set; } = true;
        public void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    JsonConvert.PopulateObject(json, this);
                    Initialized = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                string json = JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
                {
                    Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
                    NullValueHandling = NullValueHandling.Ignore
                });
                string tmpPath = SettingsPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, SettingsPath, true);
                System.Diagnostics.Debug.WriteLine($"Settings saved to {SettingsPath} ({json.Length} chars)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        [OnError]
        internal void OnError(StreamingContext context, ErrorContext errorContext)
        {
            System.Diagnostics.Debug.WriteLine("Failed to parse settings: " + errorContext.Error.Message);
            errorContext.Handled = true;
        }
    }
}
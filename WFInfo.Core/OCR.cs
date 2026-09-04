using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Tesseract;
using WFInfo.Services;
using WFInfo.Services.Screenshot;
using WFInfo.Services.WindowInfo;
using WFInfo.Settings;
using System.Runtime.InteropServices;
using WFInfo.LanguageProcessing;

namespace WFInfo
{
    public class OCR
    {
        [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
        private static extern int MallocTrim(int pad);

        [DllImport("libc.so.6")]
        private static extern int mallopt(int param, int value);

        // glibc mallopt parameter constants
        private const int M_ARENA_MAX = -8;
        private const int M_MMAP_THRESHOLD = -3;

        public static void LimitMallocArenas()
        {
            try
            {
                mallopt(M_ARENA_MAX, 2);
                mallopt(M_MMAP_THRESHOLD, 32768);
            }
            catch { }
        }

        private static void TrimNativeHeap()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { MallocTrim(0); } catch { }

            try
            {
                var debugDir = new DirectoryInfo(Path.Combine(AppMain.AppPath, "debug"));
                if (debugDir.Exists)
                    debugDir.GetFiles()
                        .Where(f => f.LastWriteTime < DateTime.Now.AddHours(-1 * _settings.ImageRetentionTime))
                        .ToList().ForEach(f => f.Delete());
            }
            catch { }
        }

        #region variables and constants

        private struct ThemeInfo
        {
            public WFColor Primary;
            public WFColor Secondary;
            public WFColor ProbeTop;
            public WFColor ProbeBot;
        }

        private static readonly ThemeInfo[] AllThemes = new ThemeInfo[]
        {
            new ThemeInfo { Primary = WFColor.FromArgb(190, 169, 102), Secondary = WFColor.FromArgb(245, 227, 173), ProbeTop = WFColor.FromArgb(189, 168, 101), ProbeBot = WFColor.FromArgb( 26,  22,  24) }, //VITRUVIAN
            new ThemeInfo { Primary = WFColor.FromArgb(153,  31,  35), Secondary = WFColor.FromArgb(255,  61,  51), ProbeTop = WFColor.FromArgb(152,  31,  35), ProbeBot = WFColor.FromArgb( 17,   4,   4) }, //STALKER
            new ThemeInfo { Primary = WFColor.FromArgb(238, 193, 105), Secondary = WFColor.FromArgb(236, 211, 162), ProbeTop = WFColor.FromArgb(237, 192, 104), ProbeBot = WFColor.FromArgb( 60,  55,  43) }, //BARUUK
            new ThemeInfo { Primary = WFColor.FromArgb( 35, 201, 245), Secondary = WFColor.FromArgb(111, 229, 253), ProbeTop = WFColor.FromArgb( 35, 200, 244), ProbeBot = WFColor.FromArgb(  7,  39,  63) }, //CORPUS
            new ThemeInfo { Primary = WFColor.FromArgb( 57, 105, 192), Secondary = WFColor.FromArgb(255, 115, 230), ProbeTop = WFColor.FromArgb( 57, 105, 191), ProbeBot = WFColor.FromArgb(  7,   9,  34) }, //FORTUNA
            new ThemeInfo { Primary = WFColor.FromArgb(255, 189, 102), Secondary = WFColor.FromArgb(255, 224, 153), ProbeTop = WFColor.FromArgb(254, 188, 101), ProbeBot = WFColor.FromArgb( 18,  27,  16) }, //GRINEER
            new ThemeInfo { Primary = WFColor.FromArgb( 36, 184, 242), Secondary = WFColor.FromArgb(255, 241, 191), ProbeTop = WFColor.FromArgb( 36, 183, 241), ProbeBot = WFColor.FromArgb( 39,  53,  96) }, //LOTUS
            new ThemeInfo { Primary = WFColor.FromArgb(140,  38,  92), Secondary = WFColor.FromArgb(245,  73,  93), ProbeTop = WFColor.FromArgb(139,  38,  91), ProbeBot = WFColor.FromArgb(220, 211, 197) }, //NIDUS
            new ThemeInfo { Primary = WFColor.FromArgb( 20,  41,  29), Secondary = WFColor.FromArgb(178, 125,   5), ProbeTop = WFColor.FromArgb( 20,  41,  29), ProbeBot = WFColor.FromArgb(203, 209, 208) }, //OROKIN
            new ThemeInfo { Primary = WFColor.FromArgb(  9,  78, 106), Secondary = WFColor.FromArgb(  6, 106,  74), ProbeTop = WFColor.FromArgb(  9,  78, 105), ProbeBot = WFColor.FromArgb(183, 204, 207) }, //TENNO
            new ThemeInfo { Primary = WFColor.FromArgb(102, 176, 255), Secondary = WFColor.FromArgb(255, 255,   0), ProbeTop = WFColor.FromArgb(101, 175, 254), ProbeBot = WFColor.FromArgb( 15,  31,  61) }, //HIGH_CONTRAST
            new ThemeInfo { Primary = WFColor.FromArgb(255, 255, 255), Secondary = WFColor.FromArgb(232, 213,  93), ProbeTop = WFColor.FromArgb(254, 254, 254), ProbeBot = WFColor.FromArgb( 35,  60,  70) }, //LEGACY
            new ThemeInfo { Primary = WFColor.FromArgb(158, 159, 167), Secondary = WFColor.FromArgb(232, 227, 227), ProbeTop = WFColor.FromArgb(157, 159, 166), ProbeBot = WFColor.FromArgb( 19,  12,  21) }, //EQUINOX
            new ThemeInfo { Primary = WFColor.FromArgb(140, 119, 147), Secondary = WFColor.FromArgb(200, 169, 237), ProbeTop = WFColor.FromArgb(139, 119, 146), ProbeBot = WFColor.FromArgb( 41,  11,  85) }, //DARK_LOTUS
            new ThemeInfo { Primary = WFColor.FromArgb(253, 132,   2), Secondary = WFColor.FromArgb(255,  53,   0), ProbeTop = WFColor.FromArgb(252, 132,   2), ProbeBot = WFColor.FromArgb( 27,  26,  27) }, //ZEPHYR
            new ThemeInfo { Primary = WFColor.FromArgb(200, 100, 200), Secondary = WFColor.FromArgb(255, 215,   0), ProbeTop = WFColor.FromArgb(254, 254, 254), ProbeBot = WFColor.FromArgb(177,  66, 182) }, //CONQUERA
            new ThemeInfo { Primary = WFColor.FromArgb( 25,  35,  60), Secondary = WFColor.FromArgb(255, 255, 255), ProbeTop = WFColor.FromArgb(254, 254, 254), ProbeBot = WFColor.FromArgb( 30,  40,  62) }, //DEADLOCK
            new ThemeInfo { Primary = WFColor.FromArgb(160,  40,  40), Secondary = WFColor.FromArgb(255, 200, 100), ProbeTop = WFColor.FromArgb(254, 254, 254), ProbeBot = WFColor.FromArgb(101,  28,  29) }, //LUNAR_RENEWAL
            new ThemeInfo { Primary = WFColor.FromArgb(105, 185, 140), Secondary = WFColor.FromArgb(100, 255, 100), ProbeTop = WFColor.FromArgb(129, 223, 150), ProbeBot = WFColor.FromArgb( 11,  47,  31) }, //POM_2
        };

        public static readonly WFColor[] ThemePrimary = AllThemes.Select(t => t.Primary).ToArray();
        public static readonly WFColor[] ThemeSecondary = AllThemes.Select(t => t.Secondary).ToArray();

        private const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowExponent;

        public static double uiScaling = 1.0;

        // Pixel measurements for reward screen @ 1920 x 1080 with 100% scale
        public const int pixleRewardWidth = 968;
        public const int pixleRewardHeight = 235;
        public const int pixleRewardYDisplay = 316;
        public const int pixelRewardLineHeight = 48;

        private static bool IsCJKLocale()
        {
            var locale = _settings?.Locale;
            if (string.IsNullOrEmpty(locale)) return false;
            return locale == "ko" || locale == "zh-hans" || locale == "zh-hant" || locale == "ja";
        }

        private static int GetAdjustedLineHeight()
        {
            return IsCJKLocale() ? 58 : pixelRewardLineHeight;
        }

        private static T SafeCall<T>(Func<T> func, T defaultValue, string operationName, string itemName)
        {
            try { return func(); }
            catch (Exception ex)
            {
                AppMain.AddLog($"ERROR: {operationName} failed for '{itemName}': {ex.Message}");
                return defaultValue;
            }
        }

        private static int numberOfRewardsDisplayed;

        public const int SCALING_LIMIT = 100;
        private static readonly object _firstEngineLock = new object();
        private static int _processingActive;
        public static bool processingActive
        {
            get => Interlocked.CompareExchange(ref _processingActive, 0, 0) != 0;
            set => Interlocked.Exchange(ref _processingActive, value ? 1 : 0);
        }
        /// <summary>UTC ticks of the last successful ProcessRewardScreen call.</summary>
        public static long LastRewardProcessedTicks;
        private static List<string> _lastProcessedRewards = new List<string>();

        private static SKBitmap bigScreenshot;
        private static SKBitmap partialScreenshot;


        private static string[] firstChecks;

        private static string timestamp;
        private static string clipboard;
        #endregion

        private static readonly char[] WordSplitChars = { ' ' };
        private static readonly string[] PrimeSplitChars = { "Prime" };
        private static readonly char[] NewlineSplitChars = { '\r', '\n' };

        public const int SnapItOverlayHeight = 105;

        private static readonly SemaphoreSlim ReloadSemaphore = new SemaphoreSlim(1, 1);
        private static ITesseractService _tesseractService;
        private static bool _tesseractInitFailed;
        private static ISoundPlayer _soundPlayer;
        private static IReadOnlyApplicationSettings _settings;
        private static IWindowInfoService _window;
        private static IScreenshotService _screenshotService;

        public static event Action<int, string, string, string, string, string, bool, bool, string, string, bool, bool, string> OnRewardDisplay;
        public static event Action<int, int, int, int, int> OnOverlayDisplay;
        public static event Action OnRewardsDoneDisplaying;
        public static event Action<List<string>> OnRewardsProcessed;
        public static event Action<string> OnClipboardCopy;

        public static void Init(ITesseractService tesseractService, ISoundPlayer soundPlayer,
            IReadOnlyApplicationSettings settings, IWindowInfoService window, IScreenshotService screenshotService)
        {
            Directory.CreateDirectory(Path.Combine(AppMain.AppPath, "debug"));
            _tesseractService = tesseractService;
            _soundPlayer = soundPlayer;
            _settings = settings;
            _window = window;
            _screenshotService = screenshotService;

            LanguageProcessorFactory.Initialize(settings);

            _tesseractInitFailed = false;
            try
            {
                _tesseractService.Init();
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ERROR: Failed to initialize TesseractService: {ex.Message}");
                _tesseractInitFailed = true;
            }
        }

        public static bool ProcessRewardScreen(SKBitmap file = null)
        {
            if (_tesseractInitFailed || _tesseractService == null)
            {
                AppMain.AddLog("ERROR: Cannot process reward screen - TesseractService is null or failed");
                return false;
            }

            if (Interlocked.CompareExchange(ref _processingActive, 1, 0) != 0)
            {
                AppMain.StatusUpdate("Still Processing Reward Screen", 2);
                return false;
            }

            var primeRewards = new List<string>();
            AppMain.StatusUpdate("Processing...", 0);
            AppMain.AddLog("----  Triggered Reward Screen Processing  ------------------------------------------------------------------");

            DateTime time = DateTime.UtcNow;
            timestamp = time.ToString("yyyy-MM-dd HH-mm-ssff", AppMain.culture);
            var watch = new Stopwatch();
            watch.Start();
            long start = watch.ElapsedMilliseconds;

            List<SKBitmap> parts;

            bool ownsScreenshot = (file == null);
            if (file != null)
            {
                bigScreenshot?.Dispose();
                bigScreenshot = file;
            }
            else
            {
                var screenshot = CaptureScreenshot();
                if (screenshot == null)
                {
                    AppMain.AddLog("Processing failed: Screenshot failed");
                    processingActive = false;
                    return false;
                }
                bigScreenshot?.Dispose();
                bigScreenshot = screenshot;
            }

            try
            {
                parts = ExtractPartBoxAutomatically(out uiScaling, out _, bigScreenshot);
            }
            catch (Exception e)
            {
                partialScreenshot?.Dispose();
                partialScreenshot = null;
                processingActive = false;
                Debug.WriteLine(e);
                return false;
            }
            finally
            {
                if (ownsScreenshot)
                    bigScreenshot?.Dispose();
                bigScreenshot = null;
            }

            int engineCount = Math.Min(parts.Count, _tesseractService.Engines.Length);
            firstChecks = new string[parts.Count];
            Task[] tasks = new Task[engineCount];
            for (int i = 0; i < engineCount; i++)
            {
                int tempI = i;
                tasks[i] = Task.Run(() => { firstChecks[tempI] = GetTextFromImage(parts[tempI], _tesseractService.Engines[tempI]); });
            }
            try
            {
                Task.WaitAll(tasks);
            }
            finally
            {
                foreach (var part in parts) part?.Dispose();
            }
            for (int i = engineCount; i < parts.Count; i++)
            {
                firstChecks[i] = GetTextFromImage(parts[i], _tesseractService.Engines[0]);
            }

            firstChecks = firstChecks.Where(s => !string.IsNullOrEmpty(s) && PartNameValid(s)).ToArray();
            if (firstChecks.Length == 0)
            {
                partialScreenshot?.Dispose();
                partialScreenshot = null;
                processingActive = false;
                AppMain.AddLog("Couldn't find rewards, processing time: " + (watch.ElapsedMilliseconds - start) + " ms");
                AppMain.StatusUpdate("Couldn't find any rewards to display", 2);
                return false;
            }

            double bestPlat = 0;
            int bestDucat = 0;
            int bestPlatItem = 0;
            int bestDucatItem = 0;
            List<int> unownedItems = new List<int>();

            try
            {
                if (firstChecks.Length > 0)
                {
                    numberOfRewardsDisplayed = firstChecks.Length;
                    clipboard = string.Empty;
                    int width = (int)(pixleRewardWidth * _window.ScreenScaling * uiScaling) + 10;
                    int startX = _window.Center.X - width / 2 + (int)(width * 0.004);
                    if (firstChecks.Length % 2 == 1) startX += width / 8;
                    if (firstChecks.Length <= 2) startX += 2 * (width / 8);
                    int overWid = (int)(width / 4.1);
                    int startY = (int)(_window.Center.Y - 20 * _window.ScreenScaling * uiScaling);
                    int partNumber = 0;
                    bool hideRewardInfo = false;
                    var pendingParts = new List<(int pn, string name, string plat, string setPlat, string ducats, string volume,
                        bool vaulted, bool mastered, string owned, bool hideInfo)>();

                    if (AppMain.dataBase?.marketData == null)
                    {
                        AppMain.AddLog("Processing failed: Market data not yet loaded");
                        return false;
                    }

                    for (int i = 0; i < firstChecks.Length; i++)
                    {
                        string part = firstChecks[i];
                        string correctName = AppMain.dataBase.GetPartName(part, out int proximity, false, out _);

                        if (proximity == 9999 || proximity > GetMaxAllowedLevenshteinDistance(part.Length) || string.IsNullOrEmpty(correctName))
                        {
                            AppMain.AddLog($"Rejected junk match: '{part}' with distance {proximity}");
                            continue;
                        }

                        string primeSetName = Data.GetSetName(correctName);
                        JObject job = (JObject)AppMain.dataBase.marketData.GetValue(correctName);
                        JObject primeSet = (JObject)AppMain.dataBase.marketData.GetValue(primeSetName);

                        if (job == null || job["ducats"] == null)
                        {
                            AppMain.AddLog($"MARKET DATA: No data found for '{correctName}', skipping");
                            continue;
                        }

                        string ducats = job["ducats"].ToObject<string>();
                        if (!int.TryParse(ducats, NumberStyles.Integer, AppMain.culture, out int ducatValue) || ducatValue == 0)
                            hideRewardInfo = true;

                        if (AppMain.dataBase.IsIgnoredItem(correctName) || AppMain.dataBase.IsIgnoredItem(part))
                            hideRewardInfo = true;

                        primeRewards.Add(correctName);
                        string plat = job["plat"].ToObject<string>();
                        string primeSetPlat = primeSet != null ? (string)primeSet["plat"] : null;
                        if (!double.TryParse(plat, styles, AppMain.culture, out double platinum))
                            platinum = 0;
                        string volume = job["volume"].ToObject<string>();
                        bool vaulted = SafeCall(() => AppMain.dataBase.IsPartVaulted(correctName), false, "IsPartVaulted", correctName);
                        bool mastered = SafeCall(() => AppMain.dataBase.IsPartMastered(correctName), false, "IsPartMastered", correctName);
                        string partsOwned = SafeCall(() => AppMain.dataBase.PartsOwned(correctName), "0", "PartsOwned", correctName);
                        string partsCount = SafeCall(() => AppMain.dataBase.PartsCount(correctName), "0", "PartsCount", correctName);
                        int duc = ducatValue;

                        if (platinum >= bestPlat)
                        {
                            bestPlat = platinum; bestPlatItem = partNumber;
                            if (duc >= bestDucat) { bestDucat = duc; bestDucatItem = partNumber; }
                        }
                        if (duc > bestDucat) { bestDucat = duc; bestDucatItem = partNumber; }
                        int.TryParse(partsOwned, System.Globalization.NumberStyles.Integer, AppMain.culture, out int ownedVal);
                        int.TryParse(partsCount, System.Globalization.NumberStyles.Integer, AppMain.culture, out int countVal);
                        if (duc > 0 && !mastered && ownedVal < countVal)
                            unownedItems.Add(partNumber);

                        if (platinum > 0)
                        {
                            if (!string.IsNullOrEmpty(clipboard)) clipboard += "-  ";
                            string localizedName = AppMain.dataBase.GetLocalizedNameForClipboard(correctName);
                            localizedName = AppMain.dataBase.RemoveBlueprintTerms(localizedName);
                            clipboard += "[" + localizedName + "]: " + plat + ":platinum: ";
                            if (primeSetPlat != null) clipboard += "Set: " + primeSetPlat + ":platinum: ";
                            if (_settings.ClipboardVaulted)
                            {
                                clipboard += ducats + ":ducats:";
                                if (vaulted) clipboard += "(V)";
                            }
                        }
                        pendingParts.Add((partNumber, correctName, plat, primeSetPlat, ducats, volume,
                            vaulted, mastered, $"{partsOwned} / {partsCount}", hideRewardInfo));
                        partNumber++;
                        hideRewardInfo = false;
                    }

                    if (!string.IsNullOrEmpty(clipboard))
                        clipboard += _settings.ClipboardTemplate;

                    if (partNumber == 0)
                    {
                        _lastProcessedRewards.Clear();
                        AppMain.AddLog("Processing failed: All items rejected as junk");
                        return false;
                    }

                    // Compute highlight per part: plat > ducat > owned
                    var highlights = new string[partNumber];
                    if (_settings.HighlightRewards)
                    {
                        foreach (int idx in unownedItems)
                            if (idx < partNumber) highlights[idx] = "owned";
                        if (bestDucatItem < partNumber) highlights[bestDucatItem] = "ducat";
                        if (bestPlatItem < partNumber) highlights[bestPlatItem] = "plat";
                    }

                    var end = watch.ElapsedMilliseconds;
                    AppMain.StatusUpdate("Completed processing (" + (end - start) + "ms)", 0);

                    AppMain.RunOnUIThread(() =>
                    {
                        foreach (var (pn, name, pl, setPl, duc, vol, vault, mast, ownedStr, hide) in pendingParts)
                        {
                            string hl = pn < highlights.Length ? highlights[pn] : null;
                            OnRewardDisplay?.Invoke(pn, name, pl, setPl, duc, vol,
                                vault, mast, ownedStr, "", hide, false, hl);
                            OnOverlayDisplay?.Invoke(pn, overWid,
                                startX + width / 4 * pn + _settings.OverlayXOffsetValue,
                                startY + _settings.OverlayYOffsetValue,
                                _settings.Delay);
                        }
                        OnRewardsDoneDisplaying?.Invoke();
                    });
                    AppMain.AddLog("Total Processing Time " + (end - start) + " ms");
                    watch.Stop();

                    if (primeRewards.Count > 0)
                    {
                        _lastProcessedRewards = new List<string>(primeRewards);
                        OnRewardsProcessed?.Invoke(primeRewards);
                    }

                    if (_settings.Clipboard && !string.IsNullOrEmpty(clipboard))
                        OnClipboardCopy?.Invoke(clipboard);
                }

                if (_settings.IsLightSelected && clipboard?.Length > 3)
                    _soundPlayer?.Play();

                if (partialScreenshot != null)
                {
                    SaveBitmap(partialScreenshot, Path.Combine(AppMain.AppPath, "debug", "PartBox " + timestamp + ".png"));
                    partialScreenshot.Dispose();
                    partialScreenshot = null;
                }
                LastRewardProcessedTicks = DateTime.UtcNow.Ticks;
                return true;
            }
            finally
            {
                if (partialScreenshot != null)
                {
                    partialScreenshot.Dispose();
                    partialScreenshot = null;
                }
                processingActive = false;
                TrimNativeHeap();
            }
        }

        public static int GetSelectedReward(int clickX, int clickY)
        {
            clickX -= _window.Window.Left;
            clickY -= _window.Window.Top;
            var width = _window.Window.Width;
            var height = _window.Window.Height;

            var scale = _window.ScreenScaling * uiScaling;
            var cardWidth = (int)(pixleRewardWidth * scale);
            var cardHeight = (int)(pixleRewardHeight * scale);
            var cardLeft = (width / 2) - (cardWidth / 2);
            var cardTop = (height / 2) - (int)(pixleRewardYDisplay * scale);
            var margin = 20;
            var selRect = new SKRectI(cardLeft - margin, cardTop - margin, cardLeft + cardWidth + margin, cardTop + cardHeight + margin);
            var midHeight = cardTop + cardHeight / 2;
            var length = cardWidth / 8;

            if (_settings.Debug)
            {
                AppMain.AddLog($"GetSelectedReward: click=({clickX},{clickY}) window={width}x{height} screenScaling={_window.ScreenScaling:F2} uiScaling={uiScaling:F2} rewards={numberOfRewardsDisplayed}");
                AppMain.AddLog($"GetSelectedReward: selRect=({selRect.Left},{selRect.Top},{selRect.Right},{selRect.Bottom}) cardWidth={cardWidth} cardLeft={cardLeft}");
            }

            if (!selRect.Contains(clickX, clickY))
                return -1;

            var primeRewardIndex = 0;

            if (numberOfRewardsDisplayed == 1)
            {
                primeRewardIndex = 0;
            }
            else if (numberOfRewardsDisplayed != 3)
            {
                var points = new (int x, int y)[] {
                    (cardLeft + length, midHeight),
                    (cardLeft + 3 * length, midHeight),
                    (cardLeft + 5 * length, midHeight),
                    (cardLeft + 7 * length, midHeight)
                };
                if (_settings.Debug)
                    AppMain.AddLog($"GetSelectedReward: centers=({points[0].x},{points[1].x},{points[2].x},{points[3].x}) midY={midHeight} boundaries=({(points[0].x + points[1].x) / 2},{(points[1].x + points[2].x) / 2},{(points[2].x + points[3].x) / 2})");
                var lowestDist = int.MaxValue;
                for (int i = 0; i < points.Length; i++)
                {
                    var dx = clickX - points[i].x;
                    var dy = clickY - points[i].y;
                    var dist = dx * dx + dy * dy;
                    if (dist < lowestDist)
                    {
                        lowestDist = dist;
                        primeRewardIndex = i;
                    }
                }
                if (numberOfRewardsDisplayed == 2)
                {
                    if (primeRewardIndex == 1) primeRewardIndex = 0;
                    if (primeRewardIndex >= 2) primeRewardIndex = 1;
                }
            }
            else
            {
                var points = new (int x, int y)[] {
                    (cardLeft + 2 * length, midHeight),
                    (cardLeft + 4 * length, midHeight),
                    (cardLeft + 6 * length, midHeight)
                };
                if (_settings.Debug)
                    AppMain.AddLog($"GetSelectedReward: centers=({points[0].x},{points[1].x},{points[2].x}) midY={midHeight} boundaries=({(points[0].x + points[1].x) / 2},{(points[1].x + points[2].x) / 2})");
                var lowestDist = int.MaxValue;
                for (int i = 0; i < points.Length; i++)
                {
                    var dx = clickX - points[i].x;
                    var dy = clickY - points[i].y;
                    var dist = dx * dx + dy * dy;
                    if (dist < lowestDist)
                    {
                        lowestDist = dist;
                        primeRewardIndex = i;
                    }
                }
            }

            return primeRewardIndex;
        }

        internal static bool PartNameValid(string partName)
        {
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            return processor?.IsPartNameValid(partName) ?? false;
        }

        private static int GetMaxAllowedLevenshteinDistance(int partNameLength)
        {
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            double ratio = processor?.DistanceThresholdRatio ?? 0.5;
            return Math.Max((int)Math.Ceiling(partNameLength * ratio), 3);
        }

        #region Theme Detection

        public static WFtheme GetThemeWeighted(out double closestThresh, SKBitmap image = null)
        {
            bool localImage = false;
            if (image == null)
            {
                image = CaptureScreenshot();
                if (image == null)
                {
                    closestThresh = 0;
                    return WFtheme.UNKNOWN;
                }
                localImage = true;
            }

            try
            {
                if (image.Height == 0)
                    throw new Exception("Image height was 0");

                double[] themeWeights = ComputeThemeWeights(image);

                double maxWeight = 0;
                WFtheme activeTheme = WFtheme.UNKNOWN;
                for (int i = 0; i < themeWeights.Length; i++)
                {
                    if (themeWeights[i] > maxWeight)
                    {
                        maxWeight = themeWeights[i];
                        activeTheme = (WFtheme)i;
                    }
                }
                AppMain.AddLog("CLOSEST THEME(" + maxWeight.ToString("F2", AppMain.culture) + "): " + activeTheme.ToString());
                closestThresh = maxWeight;
                if (_settings.ThemeSelection != WFtheme.AUTO)
                {
                    AppMain.AddLog("Theme overwrite present, setting to: " + _settings.ThemeSelection.ToString());
                    return _settings.ThemeSelection;
                }
                return activeTheme;
            }
            finally
            {
                if (localImage)
                    image?.Dispose();
            }
        }

        private static double[] ComputeThemeWeights(SKBitmap image)
        {
            int nThemes = Enum.GetValues(typeof(WFtheme)).Cast<int>().Where(v => v >= 0).Max() + 1;
            double[] weights = new double[nThemes];
            if (image == null || image.Height == 0) return weights;

            double sc = _window.ScreenScaling * Math.Max(uiScaling, 0.5);
            int probeX = (int)Math.Round(150 * sc);
            int probeY1 = (int)Math.Round(85 * sc);
            int probeY2 = (int)Math.Round(93 * sc);
            if (probeX >= image.Width || probeY1 >= image.Height) return weights;
            probeY2 = Math.Min(probeY2, image.Height - 1);
            int midY = (probeY1 + probeY2) / 2;

            var pixelSpan = image.GetPixelSpan();
            int imgWidth = image.Width;
            int lineH = probeY2 - probeY1 + 1;

            long tR = 0, tG = 0, tB = 0, tCnt = 0;
            long bR = 0, bG = 0, bB = 0, bCnt = 0;
            for (int row = 0; row < lineH; row++)
            {
                int y = probeY1 + row;
                int byteIdx = (y * imgWidth + probeX) * 4;
                int r = pixelSpan[byteIdx + 2];
                int g = pixelSpan[byteIdx + 1];
                int b = pixelSpan[byteIdx];
                if (y < midY) { tR += r; tG += g; tB += b; tCnt++; }
                else          { bR += r; bG += g; bB += b; bCnt++; }
            }

            if (tCnt == 0) { tR = 0; tG = 0; tB = 0; tCnt = 1; }
            if (bCnt == 0) { bR = 0; bG = 0; bB = 0; bCnt = 1; }
            var avgTop = WFColor.FromArgb((int)(tR / tCnt), (int)(tG / tCnt), (int)(tB / tCnt));
            var avgBot = WFColor.FromArgb((int)(bR / bCnt), (int)(bG / bCnt), (int)(bB / bCnt));

            for (int i = 0; i < nThemes; i++)
            {
                double dist = ColorDifference(avgTop, AllThemes[i].ProbeTop)
                            + ColorDifference(avgBot, AllThemes[i].ProbeBot);
                weights[i] = 1.0 / (dist + 1);
            }

            if (_settings != null && _settings.Debug)
            {
                try
                {
                    int bestIdx = 0;
                    for (int i = 1; i < nThemes; i++)
                        if (weights[i] > weights[bestIdx]) bestIdx = i;
                    AppMain.AddLog($"ProbeTheme: probe=({probeX},{probeY1}-{probeY2}) mid={midY} " +
                        $"top=({avgTop.R},{avgTop.G},{avgTop.B}) bot=({avgBot.R},{avgBot.G},{avgBot.B}) " +
                        $"scale={sc:F2} ui={uiScaling:F2} dpi={_window.ScreenScaling:F2} " +
                        $"best={(WFtheme)bestIdx} score={weights[bestIdx]:F4}");
                }
                catch { }
            }

            return weights;
        }

        private static int ColorDifference(WFColor test, WFColor thresh)
        {
            return Math.Abs(test.R - thresh.R) + Math.Abs(test.G - thresh.G) + Math.Abs(test.B - thresh.B);
        }

        #endregion

        #region Theme Threshold Filter

        public static bool CustomThresholdFilter(WFColor test)
        {
            test.GetHSB(out float tH, out float tS, out float tB);
            if (_settings.CF_usePrimaryHSL)
            {
                if (_settings.CF_pHueMax >= tH && tH >= _settings.CF_pHueMin &&
                    _settings.CF_pSatMax >= tS && tS >= _settings.CF_pSatMin &&
                    _settings.CF_pBrightMax >= tB && tB >= _settings.CF_pBrightMin)
                    return true;
            }
            if (_settings.CF_usePrimaryRGB)
            {
                if (_settings.CF_pRMax >= test.R && test.R >= _settings.CF_pRMin &&
                    _settings.CF_pGMax >= test.G && test.G >= _settings.CF_pGMin &&
                    _settings.CF_pBMax >= test.B && test.B >= _settings.CF_pBMin)
                    return true;
            }
            if (_settings.CF_useSecondaryHSL)
            {
                if (_settings.CF_sHueMax >= tH && tH >= _settings.CF_sHueMin &&
                    _settings.CF_sSatMax >= tS && tS >= _settings.CF_sSatMin &&
                    _settings.CF_sBrightMax >= tB && tB >= _settings.CF_sBrightMin)
                    return true;
            }
            if (_settings.CF_useSecondaryRGB)
            {
                if (_settings.CF_sRMax >= test.R && test.R >= _settings.CF_sRMin &&
                    _settings.CF_sGMax >= test.G && test.G >= _settings.CF_sGMin &&
                    _settings.CF_sBMax >= test.B && test.B >= _settings.CF_sBMin)
                    return true;
            }
            return false;
        }

        public static bool ThemeThresholdFilter(WFColor test, WFtheme theme)
        {
            if (theme == WFtheme.CUSTOM || theme == WFtheme.UNKNOWN)
                return CustomThresholdFilter(test);
            WFColor primary = ThemePrimary[(int)theme];
            WFColor secondary = ThemeSecondary[(int)theme];

            // Pre-compute HSB once per pixel
            test.GetHSB(out float tH, out float tS, out float tB);
            primary.GetHSB(out float pH, out float pS, out float pB);
            secondary.GetHSB(out float sH, out float sS, out float sB);

            switch (theme)
            {
                case WFtheme.VITRUVIAN:
                    return Math.Abs(tH - pH) < 4 && tS >= 0.25 && tB >= 0.42;
                case WFtheme.LOTUS:
                    return Math.Abs(tH - pH) < 5 && tS >= 0.65 && Math.Abs(tB - pB) <= 0.1
                        || (Math.Abs(tH - sH) < 15 && tB >= 0.65);
                case WFtheme.OROKIN:
                    return (Math.Abs(tH - pH) < 5 && tB <= 0.42 && tS >= 0.1)
                        || (Math.Abs(tH - sH) < 5 && tB <= 0.5 && tB >= 0.25 && tS >= 0.25);
                case WFtheme.STALKER:
                    return ((Math.Abs(tH - pH) < 4 && tS >= 0.55)
                    || (Math.Abs(tH - sH) < 4 && tS >= 0.66)) && tB >= 0.25;
                case WFtheme.CORPUS:
                    return Math.Abs(tH - pH) < 3 && tB >= 0.42 && tS >= 0.35;
                case WFtheme.EQUINOX:
                    return tS <= 0.2 && tB >= 0.55;
                case WFtheme.DARK_LOTUS:
                    return (Math.Abs(tH - pH) < 15 && tB >= 0.35 && tB <= 0.55 && tB >= 0.40 && tS <= 0.20 && tS >= 0.05)
                        || (Math.Abs(tH - sH) < 4 && tB >= 0.60 && tS >= 0.30 && tS <= 0.70);
                case WFtheme.FORTUNA:
                    return ((Math.Abs(tH - pH) < 3 && tB >= 0.35) || (Math.Abs(tH - sH) < 4 && tB >= 0.15)) && tS >= 0.20;
                case WFtheme.HIGH_CONTRAST:
                    return (Math.Abs(tH - pH) < 3 || Math.Abs(tH - sH) < 2) && tS >= 0.49 && tB >= 0.35;
                case WFtheme.LEGACY:
                    return (tB >= 0.65)
                        || (Math.Abs(tH - sH) < 6 && tB >= 0.5 && tS >= 0.5);
                case WFtheme.NIDUS:
                    return (Math.Abs(tH - (pH + 6)) < 8 && tS >= 0.30)
                    || (Math.Abs(tH - sH) < 15 && tS >= 0.55);
                case WFtheme.TENNO:
                    return (Math.Abs(tH - pH) < 3 || Math.Abs(tH - sH) < 2) && tS >= 0.38 && tB <= 0.55;
                case WFtheme.BARUUK:
                    return (Math.Abs(tH - pH) < 2) && tS > 0.25 && tB > 0.5;
                case WFtheme.GRINEER:
                    return (Math.Abs(tH - pH) < 5 && tB > 0.5)
                    || (Math.Abs(tH - sH) < 6 && tB > 0.55);
                case WFtheme.ZEPHYR:
                    return ((Math.Abs(tH - pH) < 4 && tS >= 0.55)
                        || (Math.Abs(tH - sH) < 4 && tS >= 0.66)) && tB >= 0.25;
                case WFtheme.CONQUERA:
                    return (Math.Abs(tH - pH) < 25 && tS >= 0.20 && tB >= 0.15 && tB <= 0.65)
                        || (tS <= 0.25 && tB >= 0.55);
                case WFtheme.DEADLOCK:
                    return tS <= 0.08 && tB >= 0.80;
                case WFtheme.LUNAR_RENEWAL:
                    return tS <= 0.15 && tB >= 0.85;
                case WFtheme.POM_2:
                    return Math.Abs(tH - sH) < 30 && tS >= 0.25 && tB >= 0.55;
                default:
                    return Math.Abs(tH - pH) < 2 || Math.Abs(tH - sH) < 2;
            }
        }

        #endregion

        #region Part Extraction

        // Vertical pixel offsets (at base scale) defining text band segments for reward box detection
        private static readonly int[] TextSegments = new int[] { 2, 4, 16, 21 };

        private static List<SKBitmap> ExtractPartBoxAutomatically(out double scaling, out WFtheme active, SKBitmap fullScreen)
        {
            var watch = new Stopwatch();
            watch.Start();
            long start = watch.ElapsedMilliseconds;
            long beginning = start;

            int lineHeight = (int)(GetAdjustedLineHeight() / 2 * _window.ScreenScaling);
            int width = _window.Window.Width;
            int height = _window.Window.Height;
            int mostWidth = (int)(pixleRewardWidth * _window.ScreenScaling);
            int mostLeft = (width / 2) - (mostWidth / 2);
            int mostTop = height / 2 - (int)((pixleRewardYDisplay - pixleRewardHeight + GetAdjustedLineHeight()) * _window.ScreenScaling);
            int mostBot = height / 2 - (int)((pixleRewardYDisplay - pixleRewardHeight) * _window.ScreenScaling * 0.5);

            SKBitmap preFilter;
            try
            {
                AppMain.AddLog($"Fullscreen is {fullScreen.Width}x{fullScreen.Height}, trying to clone: {mostWidth}x{mostBot - mostTop} at {mostLeft},{mostTop}");
                preFilter = CropBitmap(fullScreen, mostLeft, mostTop, mostWidth, mostBot - mostTop);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Something went wrong with getting the starting image: " + ex.ToString());
                throw;
            }

            long end = watch.ElapsedMilliseconds;
            AppMain.AddLog("Grabbed images " + (end - start) + "ms");
            start = watch.ElapsedMilliseconds;

            if (_settings.ThemeSelection != WFtheme.AUTO)
            {
                active = _settings.ThemeSelection;
            }
            else
            {
                active = GetThemeWeighted(out _, fullScreen);
            }

            end = watch.ElapsedMilliseconds;
            AppMain.AddLog("Got theme " + (end - start) + "ms");
            start = watch.ElapsedMilliseconds;

            int[] rows = new int[preFilter.Height];
            var pfSpan = preFilter.GetPixelSpan();
            int pfWidth = preFilter.Width;

            for (int y = 0; y < preFilter.Height; y++)
            {
                rows[y] = 0;
                for (int x = 0; x < pfWidth; x++)
                {
                    int byteIdx = (y * pfWidth + x) * 4;
                    var clr = WFColor.FromArgb(pfSpan[byteIdx + 2], pfSpan[byteIdx + 1], pfSpan[byteIdx]);
                    if (ThemeThresholdFilter(clr, active))
                        rows[y]++;
                }
            }

            end = watch.ElapsedMilliseconds;
            AppMain.AddLog("Filtered Image " + (end - start) + "ms");
            start = watch.ElapsedMilliseconds;

            // Weight arrays for 50-100% scaling range (index 0 = 50%, index 50 = 100%)
            double[] percWeights = new double[51];
            double[] topWeights = new double[51];
            double[] midWeights = new double[51];
            double[] botWeights = new double[51];

            int topLine_100 = preFilter.Height - lineHeight;
            int topLine_50 = lineHeight / 2;

            scaling = -1;
            double lowestWeight = 0;
            for (int i = 0; i <= 50; i++)
            {
                int yFromTop = preFilter.Height - (i * (topLine_100 - topLine_50) / 50 + topLine_50);
                int scale = (50 + i);
                int scaleWidth = preFilter.Width * scale / 100;

                int textTop = (int)(_window.ScreenScaling * TextSegments[0] * scale / 100);
                int textTopBot = (int)(_window.ScreenScaling * TextSegments[1] * scale / 100);
                int textBothBot = (int)(_window.ScreenScaling * TextSegments[2] * scale / 100);
                int textTailBot = (int)(_window.ScreenScaling * TextSegments[3] * scale / 100);

                int loc = textTop;
                for (; loc <= textTopBot; loc++)
                {
                    int idx = yFromTop + loc;
                    if (idx >= 0 && idx < rows.Length)
                        topWeights[i] += Math.Abs(scaleWidth * 0.06 - rows[idx]);
                }
                loc++;
                for (; loc < textBothBot; loc++)
                {
                    int idx = yFromTop + loc;
                    if (idx >= 0 && idx < rows.Length)
                    {
                        if (rows[idx] < scaleWidth / 15)
                            midWeights[i] += (scaleWidth * 0.26 - rows[idx]) * 5;
                        else
                            midWeights[i] += Math.Abs(scaleWidth * 0.24 - rows[idx]);
                    }
                }
                loc++;
                for (; loc < textTailBot; loc++)
                {
                    int idx = yFromTop + loc;
                    if (idx >= 0 && idx < rows.Length)
                        botWeights[i] += 10 * Math.Abs(scaleWidth * 0.007 - rows[idx]);
                }

                if (textTopBot - textTop + 1 > 0) topWeights[i] /= textTopBot - textTop + 1;
                if (textBothBot - textTopBot - 2 > 0) midWeights[i] /= textBothBot - textTopBot - 2;
                if (textTailBot - textBothBot - 1 > 0) botWeights[i] /= textTailBot - textBothBot - 1;
                percWeights[i] = topWeights[i] + midWeights[i] + botWeights[i];

                if (scaling == -1 || lowestWeight > percWeights[i])
                {
                    scaling = scale;
                    lowestWeight = percWeights[i];
                }
            }

            end = watch.ElapsedMilliseconds;
            AppMain.AddLog("Got scaling " + (end - start) + "ms");

            int[] topFive = new int[] { -1, -1, -1, -1, -1 };
            for (int i = 0; i <= 50; i++)
            {
                int match = 4;
                while (match != -1 && topFive[match] != -1 && percWeights[i] > percWeights[topFive[match]])
                    match--;
                if (match != -1)
                {
                    for (int move = 0; move < match; move++)
                        topFive[move] = topFive[move + 1];
                    topFive[match] = i;
                }
            }

            for (int i = 0; i < 5; i++)
                AppMain.AddLog("RANK " + (5 - i) + " SCALE: " + (topFive[i] + 50) + "%\t\t" + percWeights[topFive[i]].ToString("F2", AppMain.culture));

            if (_settings.Debug)
                SaveBitmap(fullScreen, Path.Combine(AppMain.AppPath, "debug", "BorderScreenshot " + timestamp + ".png"));
            if (_settings.Debug)
                SaveBitmap(preFilter, Path.Combine(AppMain.AppPath, "debug", "FullPartArea " + timestamp + ".png"));

            scaling = topFive[4] + 50;
            scaling /= 100;
            double highScaling = scaling < 1.0 ? scaling + 0.01 : scaling;
            double lowScaling = scaling > 0.5 ? scaling - 0.01 : scaling;

            int cropWidth = (int)(pixleRewardWidth * _window.ScreenScaling * highScaling);
            int cropLeft = (preFilter.Width / 2) - (cropWidth / 2);
            int cropTop = height / 2 - (int)((pixleRewardYDisplay - pixleRewardHeight + GetAdjustedLineHeight()) * _window.ScreenScaling * highScaling);
            int cropBot = height / 2 - (int)((pixleRewardYDisplay - pixleRewardHeight) * _window.ScreenScaling * lowScaling);
            int cropHei = cropBot - cropTop;
            cropTop -= mostTop;

            try
            {
                partialScreenshot = CropBitmap(preFilter, cropLeft, cropTop, cropWidth, cropHei);
                if (partialScreenshot.Height == 0 || partialScreenshot.Width == 0)
                    throw new ArithmeticException("New image was null");
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Something went wrong copying partial screenshot: " + ex.ToString());
                preFilter.Dispose();
                throw;
            }

            preFilter.Dispose();

            end = watch.ElapsedMilliseconds;
            AppMain.AddLog("Finished function " + (end - beginning) + "ms");
            SaveBitmap(partialScreenshot, Path.Combine(AppMain.AppPath, "debug", "PartialScreenshot" + timestamp + ".png"));
            return FilterAndSeparatePartsFromPartBox(partialScreenshot, active);
        }

        private static List<SKBitmap> FilterAndSeparatePartsFromPartBox(SKBitmap partBox, WFtheme active)
        {
            double weight = 0;
            double totalEven = 0;
            double totalOdd = 0;

            int width = partBox.Width;
            int height = partBox.Height;
            int[] counts = new int[height];
            var filtered = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            var srcSpan = partBox.GetPixelSpan();
            IntPtr dstPtr = filtered.GetPixels();

            unsafe
            {
                byte* dst = (byte*)dstPtr;
                for (int x = 0; x < width; x++)
                {
                    int count = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int srcIdx = (y * width + x) * 4;
                        var clr = WFColor.FromArgb(srcSpan[srcIdx + 2], srcSpan[srcIdx + 1], srcSpan[srcIdx]);
                        int dstIdx = srcIdx;
                        if (ThemeThresholdFilter(clr, active))
                        {
                            dst[dstIdx] = 0; dst[dstIdx + 1] = 0; dst[dstIdx + 2] = 0; dst[dstIdx + 3] = 255;
                            counts[y]++;
                            count++;
                        }
                        else
                        {
                            dst[dstIdx] = 255; dst[dstIdx + 1] = 255; dst[dstIdx + 2] = 255; dst[dstIdx + 3] = 255;
                        }
                    }
                    count = Math.Min(count, partBox.Height / 3);
                    // Cosine-cubed weighting to detect 3 vs 4 player reward layouts
                    double sinVal = Math.Cos(8 * x * Math.PI / partBox.Width);
                    sinVal = sinVal * sinVal * sinVal;
                    weight += sinVal * count;
                    if (sinVal < 0) totalEven -= sinVal * count;
                    else if (sinVal > 0) totalOdd += sinVal * count;
                }
            }

            // Check bottom 10% for selection border
            for (int y = height - 1; y > height * 0.9; --y)
            {
                if (counts[y] > 5 * counts[y - 1] && counts[y] > height * 2)
                {
                    var tmp = CropBitmap(filtered, 0, 0, width, y);
                    AppMain.AddLog("Possible selection border, cropping height to: " + y + " (was " + height + ")");
                    filtered.Dispose();
                    filtered = tmp;
                    height = y;
                }
            }

            if (totalEven == 0 || totalOdd == 0)
            {
                filtered.Dispose();
                AppMain.RunOnUIThread(() => AppMain.StatusUpdate("Unable to detect reward from selection screen\nScanning inventory? Hold down snap-it modifier", 1));
                throw new Exception("Unable to find any parts");
            }

            double total = totalEven + totalOdd;
            AppMain.AddLog("EVEN DISTRIBUTION: " + (totalEven / total * 100).ToString("F2", AppMain.culture) + "%");
            AppMain.AddLog("ODD DISTRIBUTION: " + (totalOdd / total * 100).ToString("F2", AppMain.culture) + "%");

            int boxWidth = partBox.Width / 4;
            int boxHeight = filtered.Height;
            int currLeft = 0;
            int playerCount = 4;

            if (totalOdd > totalEven)
            {
                currLeft = boxWidth / 2;
                playerCount = 3;
            }

            List<SKBitmap> ret = new List<SKBitmap>(playerCount);
            for (int i = 0; i < playerCount; i++)
            {
                var newBox = CropBitmap(filtered, currLeft + i * boxWidth, 0, boxWidth, boxHeight);
                ret.Add(newBox);
                if (_settings.Debug)
                    SaveBitmap(newBox, Path.Combine(AppMain.AppPath, "debug", "PartBox(" + i + ") " + timestamp + ".png"));
            }
            filtered.Dispose();
            return ret;
        }

        #endregion

        #region OCR Text Extraction

        public static string GetTextFromImage(SKBitmap image, TesseractEngine engine)
        {
            string ret = "";
            PageSegMode[] preferredModes = IsCJKLocale()
                ? new[] { PageSegMode.SingleBlock, PageSegMode.SingleColumn }
                : new[] { PageSegMode.SingleBlock };

            Dictionary<PageSegMode, string> modeResults = new Dictionary<PageSegMode, string>();
            Dictionary<PageSegMode, double> modeScores = new Dictionary<PageSegMode, double>();

            using (var pix = SKBitmapToPix(image))
            {
                foreach (var mode in preferredModes)
                {
                    try
                    {
                        using (Page page = engine.Process(pix, mode))
                        {
                            string text = page.GetText().Trim();
                            modeResults[mode] = text;
                            double score = ScoreTextResult(text, mode);
                            modeScores[mode] = score;

                            if (score > 50 && text.Length > 6 && text.Any(c =>
                                (c >= 0xAC00 && c <= 0xD7AF) ||
                                (c >= 0x4E00 && c <= 0x9FFF) ||
                                (c >= 0x3400 && c <= 0x4DBF)))
                            {
                                ret = text;
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        AppMain.AddLog($"OCR extraction failed in GetTextFromImage: {e.Message}\n{e}");
                        modeResults[mode] = "";
                        modeScores[mode] = 0;
                    }
                }
            }

            if (string.IsNullOrEmpty(ret))
            {
                var bestMode = modeScores.OrderByDescending(kvp => kvp.Value).First().Key;
                ret = modeResults[bestMode] ?? "";
            }
            return ret.Trim();
        }

        private static double ScoreTextResult(string text, PageSegMode mode)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double score = Math.Min(text.Length, 100);
            int koreanChars = text.Count(c => c >= 0xAC00 && c <= 0xD7AF);
            int cjkChars = text.Count(c => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF));
            int nonLatinChars = koreanChars + cjkChars;
            if (nonLatinChars > 0)
            {
                score += 20;
                score += Math.Min(nonLatinChars * 2, 30);
            }
            string[] lines = text.Split(NewlineSplitChars, StringSplitOptions.RemoveEmptyEntries);
            score += Math.Min(lines.Length * 5, 25);
            if (mode == PageSegMode.SingleBlock)
            {
                if (lines.Length >= 1 && lines.Length <= 3) score += 20;
                if (nonLatinChars > 0 && lines.Length >= 2) score += 15;
            }
            else if (mode == PageSegMode.SingleColumn)
            {
                if (lines.Length >= 1 && lines.Length <= 4) score += 15;
                if (nonLatinChars > 0) score += 10;
            }
            double whitespaceRatio = (double)text.Count(char.IsWhiteSpace) / text.Length;
            if (whitespaceRatio > 0.3) score -= 10;
            return Math.Max(score, 0);
        }

        #endregion

        #region Screenshot

        public static SKBitmap CaptureScreenshot()
        {
            _window.UpdateWindow();
            if (_screenshotService == null)
            {
                AppMain.AddLog("No screenshot service available");
                return null;
            }

            try
            {
                AppMain.AddLog("CaptureScreenshot: calling screenshot service...");
                var images = _screenshotService.CaptureScreenshot().GetAwaiter().GetResult();
                if (images == null || images.Count == 0)
                {
                    AppMain.AddLog("CaptureScreenshot: returned no image (null or empty list)");
                    return null;
                }
                var image = images[0];
                for (int i = 1; i < images.Count; i++)
                    images[i]?.Dispose();
                AppMain.AddLog($"CaptureScreenshot: got {image.Width}x{image.Height} image, window={_window.Window.Width}x{_window.Window.Height}, scaling={_window.ScreenScaling:F2}");
                var debugPath = Path.Combine(AppMain.AppPath, "debug",
                    "FullScreenShot " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff", AppMain.culture) + ".png");
                var copy = image.Copy();
                Task.Run(() => { try { SaveBitmap(copy, debugPath); } finally { copy.Dispose(); } });
                return image;
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Screenshot failed: " + ex.ToString());
                return null;
            }
        }

        #endregion

        #region UI Scale Config

        /// <summary>
        /// Reads the in-game UI scale from Warframe's EE.cfg (Flash.FlashDrawScale=VALUE).
        /// Returns 0.5-1.25, or -1 if unavailable.
        /// </summary>
        internal static double ReadUiScaleFromConfig()
        {
            try
            {
                string eeLogPath = PlatformPaths.FindEELogPath();
                if (eeLogPath == null) return -1;

                string cfgPath = Path.Combine(Path.GetDirectoryName(eeLogPath), "EE.cfg");
                if (!File.Exists(cfgPath)) return -1;

                foreach (string line in File.ReadLines(cfgPath))
                {
                    if (line.StartsWith("Flash.FlashDrawScale=", StringComparison.OrdinalIgnoreCase))
                    {
                        string valStr = line.Substring("Flash.FlashDrawScale=".Length).Trim();
                        if (double.TryParse(valStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double raw))
                        {
                            int percent = (int)Math.Round(raw * 100.0 / 5.0) * 5;
                            return Math.Max(0.5, Math.Min(1.25, percent / 100.0));
                        }
                        return -1;
                    }
                }
                return 1.0;
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ReadUiScaleFromConfig failed: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Checks whether Warframe's colorblind filter is enabled in EE.cfg.
        /// </summary>
        public static bool IsColorblindFilterActive()
        {
            try
            {
                string eeLogPath = PlatformPaths.FindEELogPath();
                if (eeLogPath == null) return false;

                string cfgPath = Path.Combine(Path.GetDirectoryName(eeLogPath), "EE.cfg");
                if (!File.Exists(cfgPath)) return false;

                return File.ReadAllText(cfgPath).Contains("Graphics.ColorBlindCompensation");
            }
            catch
            {
                return false;
            }
        }

        private static double DetectUiScale(int[] rowHits, int imageWidth, int imageHeight, int fullShotHeight)
        {
            var rowHeights = new List<int>();
            int i = 0;
            while (i < imageHeight)
            {
                if ((double)rowHits[i] / imageWidth > _settings.SnapRowTextDensity)
                {
                    int j = 0;
                    while (i + j < imageHeight && (double)rowHits[i + j] / imageWidth > _settings.SnapRowEmptyDensity)
                        j++;
                    if (j > 3)
                        rowHeights.Add(j);
                    i += j;
                }
                else
                {
                    i++;
                }
            }

            if (rowHeights.Count < 3)
                return -1;

            double avgRowHeight = 0;
            foreach (int h in rowHeights)
                avgRowHeight += h;
            avgRowHeight /= rowHeights.Count;

            double referenceRowHeight = GetAdjustedLineHeight() * 0.4;
            double resolutionRatio = (double)fullShotHeight / 1080.0;
            double expectedRowHeight = referenceRowHeight * resolutionRatio;

            double scale = avgRowHeight / expectedRowHeight;
            return Math.Max(0.5, Math.Min(1.0, scale));
        }

        #endregion

        #region Engine Reload

        public static async Task updateEngineAsync()
        {
            await ReloadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_tesseractService != null)
                    await Task.Run(() => _tesseractService.ReloadEngines()).ConfigureAwait(false);
                else
                    AppMain.AddLog("ERROR: Cannot reload engines - TesseractService is null");
            }
            finally { ReloadSemaphore.Release(); }
        }

        #endregion

        #region Bitmap Helpers

        private static SKBitmap CropBitmap(SKBitmap source, int x, int y, int w, int h)
        {
            x = Math.Max(0, Math.Min(x, source.Width - 1));
            y = Math.Max(0, Math.Min(y, source.Height - 1));
            w = Math.Min(w, source.Width - x);
            h = Math.Min(h, source.Height - y);
            if (w <= 0 || h <= 0)
                return new SKBitmap(1, 1);

            var dest = new SKBitmap(w, h, source.ColorType, source.AlphaType);
            using (var canvas = new SKCanvas(dest))
            {
                canvas.DrawBitmap(source, new SKRectI(x, y, x + w, y + h), new SKRect(0, 0, w, h));
            }
            return dest;
        }

        private static void SaveBitmap(SKBitmap bitmap, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = File.OpenWrite(path))
                using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                {
                    data.SaveTo(stream);
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to save bitmap: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert SKBitmap to Tesseract Pix for OCR processing.
        /// Copies raw pixel bytes directly, no PNG compression round-trip.
        /// </summary>
        private static unsafe Pix SKBitmapToPix(SKBitmap bitmap)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            var pix = Pix.Create(w, h, 32);
            var pixData = pix.GetData();
            int wpl = pixData.WordsPerLine;

            byte* src = (byte*)bitmap.GetPixels().ToPointer();
            int srcStride = bitmap.RowBytes;
            uint* dst = (uint*)pixData.Data.ToPointer();

            for (int y = 0; y < h; y++)
            {
                byte* srcRow = src + y * srcStride;
                uint* dstRow = dst + y * wpl;
                for (int x = 0; x < w; x++)
                {
                    byte b = srcRow[x * 4];
                    byte g = srcRow[x * 4 + 1];
                    byte r = srcRow[x * 4 + 2];
                    byte a = srcRow[x * 4 + 3];
                    PixData.SetDataFourByte(dstRow, x, PixData.EncodeAsRGBA(r, g, b, a));
                }
            }

            return pix;
        }

        #endregion

        #region Master-It (Profile Screen Scanning)

        /// <summary>
        /// Scan a profile/foundry screenshot to detect mastered prime items.
        /// </summary>
        public static void ProcessProfileScreen(SKBitmap fullShot)
        {
            try
            {
            var watch = Stopwatch.StartNew();
            long start = watch.ElapsedMilliseconds;

            string ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff", AppMain.culture);
            if (_settings.Debug)
                SaveBitmap(fullShot, Path.Combine(AppMain.AppPath, "debug", "ProfileImage " + ts + ".png"));

            var foundParts = FindOwnedItems(fullShot, ts, start, watch);
            for (int i = 0; i < foundParts.Count; i++)
            {
                var part = foundParts[i];
                if (!PartNameValid(part.Name + " Blueprint"))
                    continue;

                string name = AppMain.dataBase.GetPartName(part.Name + " Blueprint", out int proximity, true, out _);
                string checkName = AppMain.dataBase.GetPartName(part.Name + " prime Blueprint", out int primeProximity, true, out _);
                AppMain.AddLog($"Checking \"{part.Name.Trim()}\", ({proximity})\"{name}\", +prime ({primeProximity})\"{checkName}\"");

                if (proximity < 3 && proximity < primeProximity && part.Name.Length > 6 && name.Contains("Prime"))
                {
                    string[] nameParts = name.Split(PrimeSplitChars, 2, StringSplitOptions.None);
                    string primeName = nameParts[0] + "Prime";

                    if (AppMain.dataBase.equipmentData[primeName]?.ToObject<JObject>()?.TryGetValue("mastered", out _) == true)
                    {
                        AppMain.dataBase.equipmentData[primeName]["mastered"] = true;
                        AppMain.AddLog($"Marked \"{primeName}\" as mastered");
                    }
                    else
                    {
                        AppMain.AddLog($"Failed to mark \"{primeName}\" as mastered");
                    }
                }
            }
            AppMain.dataBase.SaveAllJSONs();
            AppMain.RunOnUIThread(() => OnMasterItComplete?.Invoke());

            long end = watch.ElapsedMilliseconds;
            if (end - start < 10000)
                AppMain.StatusUpdate($"Completed Profile Scanning({end - start}ms)", 0);
            else
                AppMain.StatusUpdate($"Lower brightness may increase speed({end - start}ms)", 1);

            watch.Stop();
            }
            finally
            {
                TrimNativeHeap();
            }
        }

        /// <summary>
        /// Event fired when master-it finishes, UI should reload equipment data.
        /// </summary>
        public static event Action OnMasterItComplete;

        private static bool ProbeProfilePixel(byte[] byteArr, int width, int x, int y, bool lowSensitivity)
        {
            int idx = (x + y * width) * 4;
            int B = byteArr[idx];
            int G = byteArr[idx + 1];
            int R = byteArr[idx + 2];
            if (lowSensitivity)
                return R > 80 && G > 80 && B > 80;
            return R > 200 && G > 200 && B > 200;
        }

        private static List<InventoryItem> FindOwnedItems(SKBitmap profileImage, string ts, long start, Stopwatch watch)
        {
            var foundItems = new List<InventoryItem>();
            int imgWidth = profileImage.Width;
            int imgHeight = profileImage.Height;
            int probeInterval = Math.Max(imgWidth / 120, 1);
            AppMain.AddLog("Using probe interval: " + probeInterval);

            var pixelSpan = profileImage.GetPixelSpan();
            byte[] byteArr = pixelSpan.ToArray();

            int nextY = 0;
            int nextYCounter = -1;
            var skipZones = new List<(int left, int right, int bottom)>();

            for (int y = 0; y < imgHeight - 1; y = (nextYCounter == 0 ? nextY : y + 1))
            {
                for (int x = 0; x < imgWidth; x += probeInterval)
                {
                    if (!ProbeProfilePixel(byteArr, imgWidth, x, y, false))
                        continue;

                    int leftEdge = -1;
                    int hits = 0;
                    int areaWidth = 0;
                    for (int tempX = Math.Max(x - probeInterval, 0); tempX < Math.Min(x + probeInterval, imgWidth); tempX++)
                    {
                        areaWidth++;
                        if (ProbeProfilePixel(byteArr, imgWidth, tempX, y, false))
                        {
                            hits++;
                            if (leftEdge == -1) leftEdge = tempX;
                        }
                    }
                    double hitRatio = (double)hits / areaWidth;
                    if (hitRatio < 0.5) continue;

                    int rightEdge = leftEdge;
                    while (rightEdge + 2 < imgWidth &&
                           (ProbeProfilePixel(byteArr, imgWidth, rightEdge + 1, y, false) ||
                            ProbeProfilePixel(byteArr, imgWidth, rightEdge + 2, y, false)))
                    {
                        rightEdge++;
                    }

                    bool failed = false;
                    foreach (var zone in skipZones)
                    {
                        if (y < zone.bottom &&
                            ((leftEdge <= zone.left && rightEdge >= zone.left) ||
                             (leftEdge >= zone.left && leftEdge <= zone.right) ||
                             (rightEdge >= zone.left && rightEdge <= zone.right)))
                        {
                            x = Math.Max(x, zone.right);
                            failed = true;
                            break;
                        }
                    }
                    if (failed) continue;

                    int topEdge = y;
                    int bottomEdge = y;
                    var hitRatios = new List<double> { 1.0 };
                    do
                    {
                        int rightMostHit = 0;
                        int leftMostHit = -1;
                        hits = 0;
                        bottomEdge++;
                        if (bottomEdge >= imgHeight) break;
                        for (int i = leftEdge; i < rightEdge; i++)
                        {
                            if (ProbeProfilePixel(byteArr, imgWidth, i, bottomEdge, false))
                            {
                                hits++;
                                rightMostHit = i;
                                if (leftMostHit == -1) leftMostHit = i;
                            }
                        }
                        hitRatio = hits / (double)(rightEdge - leftEdge);
                        hitRatios.Add(hitRatio);

                        if (hitRatio > 0.2 && rightMostHit + 1 < rightEdge && rightEdge - leftEdge > 100)
                        {
                            rightEdge = rightMostHit;
                            bottomEdge = y;
                            hitRatios.Clear();
                            hitRatios.Add(1);
                        }
                        if (hitRatio > 0.2 && leftMostHit > leftEdge && rightEdge - leftEdge > 100)
                        {
                            leftEdge = leftMostHit;
                            bottomEdge = y;
                            hitRatios.Clear();
                            hitRatios.Add(1);
                        }
                    } while (bottomEdge + 2 < imgHeight && hitRatios[hitRatios.Count - 1] > 0.2);

                    hitRatios.RemoveAt(hitRatios.Count - 1);

                    // Look for text-gap-text pattern (4 ratio changes)
                    int ratioChanges = 0;
                    bool prevMostlyHits = true;
                    int lineBreak = -1;
                    for (int i = 0; i < hitRatios.Count; i++)
                    {
                        if ((hitRatios[i] > 0.99) != prevMostlyHits)
                        {
                            if (ratioChanges == 1) lineBreak = i + 1;
                            prevMostlyHits = !prevMostlyHits;
                            ratioChanges++;
                        }
                    }

                    int width = rightEdge - leftEdge;
                    int height = bottomEdge - topEdge;

                    // Valid inventory item labels have 2.4:1 to 4:1 aspect ratio
                    if (ratioChanges != 4 || width < 2.4 * height || width > 4 * height)
                    {
                        x = Math.Max(rightEdge, x);
                        if (watch.ElapsedMilliseconds - start > 10000)
                            AppMain.StatusUpdate("High noise, this might be slow", 3);
                        continue;
                    }

                    skipZones.Add((leftEdge, rightEdge, bottomEdge));
                    x = rightEdge;
                    nextY = bottomEdge + 1;
                    nextYCounter = Math.Max(height / 8, 3);

                    height = lineBreak;

                    // Build inverted bitmap for OCR with letter spacing
                    using var cloneBitmap = new SKBitmap(width * 3, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    int cloneStride = width * 3;
                    unsafe
                    {
                        byte* clonePtr = (byte*)cloneBitmap.GetPixels();
                        int totalBytes = cloneStride * height * 4;
                        for (int ci = 0; ci < totalBytes; ci++)
                            clonePtr[ci] = 255;

                        int offset = 0;
                        bool prevHit = false;
                        for (int i = 0; i < width; i++)
                        {
                            bool hitSomething = false;
                            for (int j = 0; j < height; j++)
                            {
                                if (!ProbeProfilePixel(byteArr, imgWidth, leftEdge + i, topEdge + j, true))
                                {
                                    if (i + offset < cloneStride && j < height)
                                    {
                                        int ci = (j * cloneStride + i + offset) * 4;
                                        clonePtr[ci] = 0; clonePtr[ci + 1] = 0; clonePtr[ci + 2] = 0;
                                    }
                                    hitSomething = true;
                                }
                            }
                            if (!hitSomething && prevHit)
                                offset += 2;
                            prevHit = hitSomething;
                        }
                    }

                    lock (_firstEngineLock)
                    {
                        using (var pix = SKBitmapToPix(cloneBitmap))
                        using (var page = _tesseractService.FirstEngine.Process(pix, PageSegMode.SingleLine))
                        using (var iterator = page.GetIterator())
                        {
                            iterator.Begin();
                            string rawText = iterator.GetText(PageIteratorLevel.TextLine);
                            rawText = System.Text.RegularExpressions.Regex.Replace(rawText ?? "", @"\s", "");
                            foundItems.Add(new InventoryItem(rawText, new SKRectI(leftEdge, topEdge, leftEdge + width, topEdge + height)));
                        }
                    }
                    TrimNativeHeap();
                }
                if (nextYCounter >= 0)
                    nextYCounter--;
            }

            if (_settings.Debug)
                SaveBitmap(profileImage, Path.Combine(AppMain.AppPath, "debug", "ProfileImageBounds " + ts + ".png"));

            return foundItems;
        }

        #endregion

        #region Snap-It

        public static event Action<string, string, string, string, string, bool, bool, string, string, bool, bool, int, int, int> OnSnapItRewardDisplay;
        public static event Action<List<InventoryItem>> OnSnapItVerifyCount;

        public static SKBitmap ScaleUpAndFilter(SKBitmap image, WFtheme active, out int[] rowHits, out int[] colHits)
        {
            SKBitmap workImage = image;
            bool scaled = false;
            if (image.Height <= SCALING_LIMIT)
            {
                int newW = image.Width * SCALING_LIMIT / image.Height;
                int newH = SCALING_LIMIT;
                var scaledBmp = new SKBitmap(newW, newH, image.ColorType, image.AlphaType);
                using (var canvas = new SKCanvas(scaledBmp))
                {
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                        canvas.DrawBitmap(image, new SKRect(0, 0, newW, newH), paint);
                }
                workImage = scaledBmp;
                scaled = true;
            }

            int width = workImage.Width;
            int height = workImage.Height;
            var filtered = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            rowHits = new int[height];
            colHits = new int[width];

            var srcSpan = workImage.GetPixelSpan();
            IntPtr dstPtr = filtered.GetPixels();

            unsafe
            {
                byte* dst = (byte*)dstPtr;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int byteIdx = (y * width + x) * 4;
                        var clr = WFColor.FromRgb(srcSpan[byteIdx + 2], srcSpan[byteIdx + 1], srcSpan[byteIdx]);
                        if (ThemeThresholdFilter(clr, active))
                        {
                            dst[byteIdx] = 0; dst[byteIdx + 1] = 0; dst[byteIdx + 2] = 0; dst[byteIdx + 3] = 255;
                            rowHits[y]++;
                            colHits[x]++;
                        }
                        else
                        {
                            dst[byteIdx] = 255; dst[byteIdx + 1] = 255; dst[byteIdx + 2] = 255; dst[byteIdx + 3] = 255;
                        }
                    }
                }
            }

            if (scaled) workImage.Dispose();
            return filtered;
        }

        private static List<(SKBitmap bitmap, SKRectI rect)> DivideSnapZones(SKBitmap filteredImageClean, int[] rowHits, int[] colHits)
        {
            int width = filteredImageClean.Width;
            int height = filteredImageClean.Height;
            var zones = new List<(SKBitmap, SKRectI)>();

            var rows = new List<(int top, int height)>();
            int i = 0;
            int rowHeight = 0;
            while (i < height)
            {
                if ((double)rowHits[i] / width > _settings.SnapRowTextDensity)
                {
                    int j = 0;
                    while (i + j < height && (double)rowHits[i + j] / width > _settings.SnapRowEmptyDensity)
                        j++;
                    if (j > 3)
                    {
                        rows.Add((i, j));
                        rowHeight += j;
                    }
                    i += j;
                }
                else { i++; }
            }
            rowHeight = rowHeight / Math.Max(rows.Count, 1);

            // Combine adjacent rows, draw separators for Tesseract
            i = 0;
            using (var canvas = new SKCanvas(filteredImageClean))
            {
                using var whitePaint = new SKPaint { Color = SKColors.White, StrokeWidth = 1, IsAntialias = false };
                while (i + 1 < rows.Count)
                {
                    canvas.DrawLine(0, rows[i].top + rows[i].height, width, rows[i].top + rows[i].height, whitePaint);
                    if (rows[i].top + rows[i].height + rowHeight > rows[i + 1].top)
                    {
                        rows[i + 1] = (rows[i].top, rows[i + 1].top - rows[i].top + rows[i + 1].height);
                        rows.RemoveAt(i);
                    }
                    else { i++; }
                }
            }

            var cols = new List<(int left, int width)>();
            int colStart = 0;
            i = 0;
            while (i + 1 < width)
            {
                if ((double)colHits[i] / height < _settings.SnapColEmptyDensity)
                {
                    int j = 0;
                    while (i + j + 1 < width && (double)colHits[i + j] / height < _settings.SnapColEmptyDensity)
                        j++;
                    if (j > rowHeight / 2)
                    {
                        if (i != 0) cols.Add((colStart, i - colStart));
                        colStart = i + j + 1;
                    }
                    i += j;
                }
                i++;
            }
            if (i != colStart) cols.Add((colStart, i - colStart));

            for (i = 0; i < rows.Count; i++)
            {
                for (int j = 0; j < cols.Count; j++)
                {
                    int top = Math.Max(rows[i].top - rowHeight / 2, 0);
                    int h = Math.Min(rows[i].height + rowHeight, height - top - 1);
                    int left = Math.Max(cols[j].left - rowHeight / 4, 0);
                    int w = Math.Min(cols[j].width + rowHeight / 2, width - left - 1);
                    if (w <= 0 || h <= 0) continue;
                    var rect = new SKRectI(left, top, left + w, top + h);
                    var crop = CropBitmap(filteredImageClean, left, top, w, h);
                    zones.Add((crop, rect));
                }
            }
            return zones;
        }



        private static List<(string text, SKRectI bounds)> GetTextWithBoundsFromImage(TesseractEngine engine, SKBitmap image, int offsetX, int offsetY)
        {
            var results = new List<(string, SKRectI)>();

            double scale = 1.0;
            SKBitmap scaledImage = null;
            if (image.Height < 80)
            {
                scale = Math.Max(2.0, Math.Ceiling(80.0 / image.Height));
                int sw = (int)(image.Width * scale);
                int sh = (int)(image.Height * scale);
                scaledImage = new SKBitmap(sw, sh, image.ColorType, image.AlphaType);
                using (var canvas = new SKCanvas(scaledImage))
                {
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                        canvas.DrawBitmap(image, new SKRect(0, 0, sw, sh), paint);
                }
            }

            try
            {
                using (var pix = SKBitmapToPix(scaledImage ?? image))
                using (var page = engine.Process(pix, PageSegMode.SparseText))
                using (var iterator = page.GetIterator())
                {
                    iterator.Begin();
                    do
                    {
                        string currentWord = iterator.GetText(PageIteratorLevel.TextLine);
                        iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect tempbounds);
                        var bounds = new SKRectI(
                            (int)(tempbounds.X1 / scale) + offsetX,
                            (int)(tempbounds.Y1 / scale) + offsetY,
                            (int)((tempbounds.X1 + tempbounds.Width) / scale) + offsetX,
                            (int)((tempbounds.Y1 + tempbounds.Height) / scale) + offsetY);
                        if (currentWord != null)
                        {
                            currentWord = currentWord.Trim();
                            if (currentWord.Length > 0)
                                results.Add((currentWord, bounds));
                        }
                    }
                    while (iterator.Next(PageIteratorLevel.TextLine));
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"OCR extraction failed in GetTextWithBoundsFromImage: {ex.Message}\n{ex}");
            }
            finally
            {
                scaledImage?.Dispose();
            }
            return results;
        }

        private static int ComputeMaxColumnWidth(int[] colHits, int[] rowHits, int imageWidth, int imageHeight, double screenScale)
        {
            int totalRowHeight = 0, rowCount = 0;
            int ri = 0;
            while (ri < imageHeight)
            {
                if ((double)rowHits[ri] / imageWidth > _settings.SnapRowTextDensity)
                {
                    int rj = 0;
                    while (ri + rj < imageHeight && (double)rowHits[ri + rj] / imageWidth > _settings.SnapRowEmptyDensity)
                        rj++;
                    if (rj > 3) { totalRowHeight += rj; rowCount++; }
                    ri += rj;
                }
                else ri++;
            }
            int avgRowHeight = rowCount > 0 ? totalRowHeight / rowCount : 30;

            int maxWidth = 0;
            int colStart = 0;
            int ci = 0;
            while (ci + 1 < imageWidth)
            {
                if ((double)colHits[ci] / imageHeight < _settings.SnapColEmptyDensity)
                {
                    int cj = 0;
                    while (ci + cj + 1 < imageWidth && (double)colHits[ci + cj] / imageHeight < _settings.SnapColEmptyDensity)
                        cj++;
                    if (cj > avgRowHeight / 2)
                    {
                        if (ci != 0) maxWidth = Math.Max(maxWidth, ci - colStart);
                        colStart = ci + cj + 1;
                    }
                    ci += cj;
                }
                ci++;
            }
            if (ci != colStart) maxWidth = Math.Max(maxWidth, ci - colStart);

            if (maxWidth > 0)
                return (int)(maxWidth * 1.2);
            return (int)(180 * screenScale);
        }

        private static List<InventoryItem> FindAllParts(SKBitmap filteredImage, SKBitmap unfilteredImage, int[] rowHits, int[] colHits)
        {
            SKBitmap filteredImageClean = null;
            try
            {
            filteredImageClean = filteredImage.Copy();
            var foundItems = new List<(List<InventoryItem> items, SKRectI bounds)>();
            var processor = LanguageProcessorFactory.GetCurrentProcessor();

            List<(SKBitmap bitmap, SKRectI rect)> zones;
            int snapThreads;
            if (_settings.SnapMultiThreaded)
            {
                zones = DivideSnapZones(filteredImageClean, rowHits, colHits);
                snapThreads = zones.Count > 8 ? 1 : 4;
            }
            else
            {
                zones = new List<(SKBitmap, SKRectI)>
                {
                    (filteredImageClean, new SKRectI(0, 0, filteredImageClean.Width, filteredImageClean.Height))
                };
                snapThreads = 1;
            }

            var snapTasks = new Task<List<(string, SKRectI)>>[snapThreads];
            for (int ti = 0; ti < snapThreads; ti++)
            {
                int tempI = ti;
                snapTasks[ti] = Task.Run(() =>
                {
                    var taskResults = new List<(string, SKRectI)>();
                    for (int j = tempI; j < zones.Count; j += snapThreads)
                        taskResults.AddRange(GetTextWithBoundsFromImage(_tesseractService.Engines[tempI], zones[j].bitmap, zones[j].rect.Left, zones[j].rect.Top));
                    return taskResults;
                });
            }
            try
            {
                Task.WaitAll(snapTasks);
            }
            finally
            {
                foreach (var zone in zones)
                {
                    if (!ReferenceEquals(zone.bitmap, filteredImageClean))
                        zone.bitmap?.Dispose();
                }
            }

            double screenScale = _window?.ScreenScaling ?? 1.0;
            int maxGroupWidth = ComputeMaxColumnWidth(colHits, rowHits, filteredImage.Width, filteredImage.Height, screenScale);
            bool isCJK = IsCJKLocale();
            int sizeThresholdH = isCJK ? (int)(80 * screenScale) : (int)(50 * screenScale);
            int sizeThresholdW = isCJK ? (int)(120 * screenScale) : (int)(84 * screenScale);
            int minCharLength = isCJK ? 2 : 3;

            for (int threadNum = 0; threadNum < snapThreads; threadNum++)
            {
                foreach (var wordResult in snapTasks[threadNum].Result)
                {
                    string currentLine = wordResult.Item1;
                    SKRectI bounds = wordResult.Item2;

                    // Filter words
                    var words = currentLine.Split(WordSplitChars, StringSplitOptions.RemoveEmptyEntries);
                    var filteredWords = new List<string>();
                    foreach (var word in words)
                    {
                        if (processor == null || !processor.ShouldFilterWord(word))
                            filteredWords.Add(word);
                    }
                    if (filteredWords.Count == 0) continue;
                    string currentWord = string.Join(" ", filteredWords);

                    int bW = bounds.Width, bH = bounds.Height;
                    int vertPad = isCJK ? bH * 3 / 4 : bH / 2;
                    double hMargin = isCJK ? Math.Min(_settings.SnapItHorizontalNameMargin, 0.3)
                        : Math.Max(_settings.SnapItHorizontalNameMargin, 0.2);
                    int hPad = (int)(bH * hMargin);

                    var paddedBounds = new SKRectI(bounds.Left - hPad, bounds.Top - vertPad,
                        bounds.Right + hPad, bounds.Bottom + vertPad);

                    if ((paddedBounds.Height > sizeThresholdH || paddedBounds.Width > sizeThresholdW) && currentWord.Length <= minCharLength)
                        continue;

                    int idx = foundItems.Count - 1;
                    for (; idx >= 0; idx--)
                    {
                        if (foundItems[idx].bounds.IntersectsWith(paddedBounds))
                        {
                            int combinedLeft = Math.Min(foundItems[idx].bounds.Left, paddedBounds.Left);
                            int combinedRight = Math.Max(foundItems[idx].bounds.Right, paddedBounds.Right);
                            if (combinedRight - combinedLeft <= maxGroupWidth) break;
                        }
                    }

                    // Proximity merge fallback
                    if (idx == -1 && foundItems.Count > 0)
                    {
                        int bestIdx = -1, bestGap = int.MaxValue;
                        int minMergeLen = isCJK ? 3 : 5;
                        for (int p = foundItems.Count - 1; p >= 0; p--)
                        {
                            if (foundItems[p].items.Count != 1) continue;
                            if (foundItems[p].items[0].Name.Length < minMergeLen || currentWord.Length < minMergeLen) continue;
                            var groupBounds = foundItems[p].bounds;
                            int vertGap = Math.Max(0, Math.Max(paddedBounds.Top - groupBounds.Bottom, groupBounds.Top - paddedBounds.Bottom));
                            int avgHeight = (paddedBounds.Height + groupBounds.Height) / 2;
                            if (vertGap <= avgHeight && vertGap < bestGap)
                            {
                                int overlapLeft = Math.Max(paddedBounds.Left, groupBounds.Left);
                                int overlapRight = Math.Min(paddedBounds.Right, groupBounds.Right);
                                if (overlapRight > overlapLeft)
                                {
                                    int cLeft = Math.Min(groupBounds.Left, paddedBounds.Left);
                                    int cRight = Math.Max(groupBounds.Right, paddedBounds.Right);
                                    if (cRight - cLeft <= maxGroupWidth)
                                    {
                                        bestIdx = p;
                                        bestGap = vertGap;
                                    }
                                }
                            }
                        }
                        if (bestIdx >= 0) idx = bestIdx;
                    }

                    if (idx == -1)
                    {
                        foundItems.Add((new List<InventoryItem> { new InventoryItem(currentWord, paddedBounds) }, paddedBounds));
                    }
                    else
                    {
                        int left = Math.Min(foundItems[idx].bounds.Left, paddedBounds.Left);
                        int top = Math.Min(foundItems[idx].bounds.Top, paddedBounds.Top);
                        int right = Math.Max(foundItems[idx].bounds.Right, paddedBounds.Right);
                        int bot = Math.Max(foundItems[idx].bounds.Bottom, paddedBounds.Bottom);
                        var combinedBounds = new SKRectI(left, top, right, bot);
                        var tempList = new List<InventoryItem>(foundItems[idx].items);
                        tempList.Add(new InventoryItem(currentWord, paddedBounds));
                        foundItems.RemoveAt(idx);
                        foundItems.Add((tempList, combinedBounds));
                    }
                }
            }

            var results = new List<InventoryItem>();
            foreach (var itemGroup in foundItems)
            {
                itemGroup.items.Sort((i1, i2) =>
                    Math.Abs(i1.Bounding.Top - i2.Bounding.Top) > i1.Bounding.Height / 8
                        ? i1.Bounding.Top - i2.Bounding.Top
                        : i1.Bounding.Left - i2.Bounding.Left);
                string name = string.Join(" ", itemGroup.items.Select(ii => ii.Name)).Trim();
                results.Add(new InventoryItem(name, itemGroup.bounds));
            }

            if (_settings.DoSnapItCount)
                GetItemCounts(filteredImageClean, unfilteredImage, results);

            filteredImageClean.Dispose();
            filteredImageClean = null;
            return results;
            }
            finally
            {
                filteredImageClean?.Dispose();
            }
        }

        private static void GetItemCounts(SKBitmap filteredImageClean, SKBitmap unfilteredImage, List<InventoryItem> foundItems)
        {
            AppMain.AddLog($"Starting Item Counting (items={foundItems.Count}, filtered={filteredImageClean.Width}x{filteredImageClean.Height}, unfiltered={unfilteredImage.Width}x{unfilteredImage.Height})");
            double screenScale = _window?.ScreenScaling ?? 1.0;

            var foundItemsBottom = foundItems.OrderBy(o => o.Bounding.Bottom).ToList();
            foundItemsBottom.RemoveAll(item => !PartNameValid(item.Name));
            var foundItemsLeft = foundItemsBottom.OrderBy(o => o.Bounding.Left).ToList();

            // Build grid for item count detection
            var gridRows = new List<SKRectI>();
            var gridCols = new List<SKRectI>();

            for (int i = 0; i < foundItemsBottom.Count; i++)
            {
                var currRow = new SKRectI(0, foundItemsBottom[i].Bounding.Top, 10000, foundItemsBottom[i].Bounding.Bottom);
                var currCol = new SKRectI(foundItemsLeft[i].Bounding.Left, 0, foundItemsLeft[i].Bounding.Right, 10000);

                if (gridRows.Count == 0 || !gridRows.Last().IntersectsWith(currRow))
                    gridRows.Add(currRow);
                else
                {
                    var last = gridRows.Last();
                    if (currRow.Bottom < last.Bottom)
                        gridRows[gridRows.Count - 1] = new SKRectI(0, last.Top, 10000, currRow.Bottom);
                    if (gridRows.Count != 1 && gridCols.Count > 0 && currCol.Top > gridCols.Last().Top)
                        gridRows[gridRows.Count - 1] = new SKRectI(0, currRow.Top, 10000, gridRows.Last().Bottom);
                }

                if (gridCols.Count == 0 || !gridCols.Last().IntersectsWith(currCol))
                    gridCols.Add(currCol);
                else
                {
                    var last = gridCols.Last();
                    if (currCol.Right < last.Right)
                        gridCols[gridCols.Count - 1] = new SKRectI(last.Left, 0, currCol.Right, 10000);
                    if (gridCols.Count != 1 && currCol.Left > gridCols.Last().Left)
                        gridCols[gridCols.Count - 1] = new SKRectI(currCol.Left, 0, gridCols.Last().Right, 10000);
                }
            }

            AppMain.AddLog($"  Count grid: {gridRows.Count} rows x {gridCols.Count} cols");
            double widthMult = _settings.DoCustomNumberBoxWidth ? _settings.SnapItNumberBoxWidth : 0.4;
            int imgWidth = filteredImageClean.Width;
            int imgHeight = filteredImageClean.Height;

            byte[] filteredBytes = filteredImageClean.GetPixelSpan().ToArray();
            byte[] unfilteredBytes = unfilteredImage.GetPixelSpan().ToArray();
            int unfilteredWidth = unfilteredImage.Width;

            for (int ri = 0; ri < gridRows.Count; ri++)
            {
                for (int ci = 0; ci < gridCols.Count; ci++)
                {
                    int left = ci == 0 ? 0 : (gridCols[ci - 1].Right + gridCols[ci].Left) / 2;
                    int top = ri == 0 ? 0 : gridRows[ri - 1].Bottom;
                    int w = Math.Min((int)((gridCols[ci].Right - left) * widthMult), imgWidth - left);
                    int h = Math.Min((gridRows[ri].Bottom - top) / 3, imgHeight - top);
                    if (w <= 0 || h <= 0) { AppMain.AddLog($"  Count[{ri},{ci}]: skip (w={w} h={h})"); continue; }

                    // Find center of mass for black pixels
                    int xCenter = 0, yCenter = 0, sumBlack = 1;
                    for (int y = top; y < top + h && y < imgHeight; y++)
                    {
                        for (int x = left; x < left + w && x < imgWidth; x++)
                        {
                            int pidx = (y * imgWidth + x) * 4;
                            if (pidx + 3 < filteredBytes.Length &&
                                filteredBytes[pidx] == 0 && filteredBytes[pidx + 1] == 0 &&
                                filteredBytes[pidx + 2] == 0 && filteredBytes[pidx + 3] == 255)
                            {
                                xCenter += x - left;
                                yCenter += y - top;
                                sumBlack++;
                            }
                        }
                    }
                    if (sumBlack < h) { AppMain.AddLog($"  Count[{ri},{ci}]: no checkmark (blackPx={sumBlack}, need>={h})"); continue; }
                    xCenter /= sumBlack;
                    yCenter /= sumBlack;

                    // Flood-fill to find checkmark icon and get its rightmost edge
                    int startX = xCenter, startY = yCenter;
                    int minToEdge = Math.Min(Math.Min(xCenter, w - xCenter), Math.Min(yCenter, h - yCenter));
                    for (int dist = 0; dist < minToEdge; dist++)
                    {
                        bool found = false;
                        foreach (var (dx, dy) in new[] { (dist, 0), (-dist, 0), (0, dist), (0, -dist) })
                        {
                            int cx = xCenter + dx, cy = yCenter + dy;
                            int pidx = ((top + cy) * imgWidth + (left + cx)) * 4;
                            if (pidx >= 0 && pidx + 3 < filteredBytes.Length &&
                                filteredBytes[pidx] == 0 && filteredBytes[pidx + 1] == 0 &&
                                filteredBytes[pidx + 2] == 0 && filteredBytes[pidx + 3] == 255)
                            {
                                startX = cx; startY = cy; found = true; break;
                            }
                        }
                        if (found) break;
                    }

                    int rightmost = 0, xCNew = startX, yCNew = startY;
                    sumBlack = 1;
                    var stack = new Stack<(int x, int y)>();
                    var visited = new HashSet<(int, int)>();
                    stack.Push((startX, startY));
                    while (stack.Count > 0)
                    {
                        var (px, py) = stack.Pop();
                        if (!visited.Add((px, py))) continue;
                        for (int xOff = -2; xOff <= 2; xOff++)
                        {
                            for (int yOff = -2; yOff <= 2; yOff++)
                            {
                                int nx = px + xOff, ny = py + yOff;
                                if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                                {
                                    int pidx = ((top + ny) * imgWidth + (left + nx)) * 4;
                                    if (pidx >= 0 && pidx + 3 < filteredBytes.Length &&
                                        filteredBytes[pidx] == 0 && filteredBytes[pidx + 1] == 0 &&
                                        filteredBytes[pidx + 2] == 0 && filteredBytes[pidx + 3] == 255)
                                    {
                                        stack.Push((nx, ny));
                                        xCNew += nx; yCNew += ny; sumBlack++;
                                        if (nx > rightmost) rightmost = nx;
                                    }
                                }
                            }
                        }
                    }
                    if (sumBlack < h) { AppMain.AddLog($"  Count[{ri},{ci}]: flood-fill too small ({sumBlack}<{h})"); continue; }
                    xCNew /= sumBlack; yCNew /= sumBlack;

                    // Y-center refinement: scan ±5 pixels vertically to center on checkmark
                    int lowest = yCNew + 1000;
                    int highest = yCNew - 1000;
                    for (int yOff = -5; yOff < 5; yOff++)
                    {
                        int checkY = yCNew + yOff;
                        if (checkY > 0 && checkY < h)
                        {
                            int pidx = ((top + checkY) * imgWidth + (left + xCNew)) * 4;
                            if (pidx >= 0 && pidx + 3 < filteredBytes.Length &&
                                filteredBytes[pidx] == 0 && filteredBytes[pidx + 1] == 0 &&
                                filteredBytes[pidx + 2] == 0 && filteredBytes[pidx + 3] == 255)
                            {
                                if (checkY > highest) highest = checkY;
                                if (checkY < lowest) lowest = checkY;
                            }
                        }
                    }
                    if (highest >= lowest)
                        yCNew = (highest + lowest) / 2;

                    AppMain.AddLog($"  Count[{ri},{ci}]: checkmark found at ({xCNew},{yCNew}), rightmost={rightmost}, black={sumBlack}");

                    // Find background color of amount label by diagonal probing
                    var colorHits = new Dictionary<uint, int>();
                    var pointsToCheck = new Queue<(int x, int y)>();
                    pointsToCheck.Enqueue((left + xCNew, top + yCNew + 1));
                    pointsToCheck.Enqueue((left + xCNew, top + yCNew - 1));
                    bool probeStop = false;
                    int probeYRef = top + yCenter;
                    while (pointsToCheck.Count > 0)
                    {
                        var (px, py) = pointsToCheck.Dequeue();
                        int offset = (py > probeYRef) ? 1 : -1;
                        // Stop 3 pixels from grid cell edge
                        if (px + 3 > left + w || px - 3 < left || py + 3 > top + h || py - 3 < top)
                            probeStop = true;
                        if (!probeStop)
                            pointsToCheck.Enqueue((px + offset, py + offset));
                        int pidx = (py * unfilteredWidth + px) * 4;
                        if (pidx < 4 || pidx + 7 >= unfilteredBytes.Length) continue;
                        // Check 3 adjacent pixels match (BGR only, alpha may be 0)
                        if (unfilteredBytes[pidx] == unfilteredBytes[pidx - 4] && unfilteredBytes[pidx] == unfilteredBytes[pidx + 4] &&
                            unfilteredBytes[pidx + 1] == unfilteredBytes[pidx - 3] && unfilteredBytes[pidx + 1] == unfilteredBytes[pidx + 5] &&
                            unfilteredBytes[pidx + 2] == unfilteredBytes[pidx - 2] && unfilteredBytes[pidx + 2] == unfilteredBytes[pidx + 6])
                        {
                            // Use RGB-only key (mask out alpha) for cross-platform compatibility
                            uint key = ((uint)unfilteredBytes[pidx + 2] << 16) |
                                       ((uint)unfilteredBytes[pidx + 1] << 8) | unfilteredBytes[pidx];
                            colorHits.TryGetValue(key, out int cnt);
                            colorHits[key] = cnt + 1;
                        }
                    }
                    if (colorHits.Count == 0) continue;

                    uint topColorKey = 0; int topScore = 0;
                    foreach (var kv in colorHits)
                    {
                        if (kv.Value > topScore) { topColorKey = kv.Key; topScore = kv.Value; }
                    }
                    byte tcB = (byte)(topColorKey & 0xFF);
                    byte tcG = (byte)((topColorKey >> 8) & 0xFF);
                    byte tcR = (byte)((topColorKey >> 16) & 0xFF);
                    AppMain.AddLog($"  Count[{ri},{ci}]: bgColor=({tcR},{tcG},{tcB}), hits={topScore}");
                    if (tcR == 255 && tcG == 255 && tcB == 255) { AppMain.AddLog($"  Count[{ri},{ci}]: skip (white bg)"); continue; }

                    // Convert to global coordinates
                    int absRightmost = left + rightmost + 1;
                    int gxCenter = left + xCenter;
                    int gyCenter = top + yCenter;
                    int gxCNew = left + xCNew;
                    int gyCNew = top + yCNew;

                    // Search diagonally for label color
                    int sx = gxCNew, sy = gyCNew;
                    {
                        int pidx = (sy * unfilteredWidth + sx) * 4;
                        bool colorMatch(int p) => p >= 0 && p + 3 < unfilteredBytes.Length &&
                            unfilteredBytes[p] == tcB && unfilteredBytes[p + 1] == tcG && unfilteredBytes[p + 2] == tcR;
                        while (sx < unfilteredWidth && sy > 0 && !colorMatch(pidx))
                        {
                            sx++; sy--;
                            pidx = (sy * unfilteredWidth + sx) * 4;
                        }
                    }
                    if (sx >= unfilteredWidth || sy <= 0) { AppMain.AddLog($"  Count[{ri},{ci}]: diagonal label probe failed"); continue; }

                    // Find label bounds
                    // Helper: check if pixel at (x,y) matches the label background color (BGR only)
                    bool isLabelColor(int bx, int by)
                    {
                        int p = (by * unfilteredWidth + bx) * 4;
                        if (p < 0 || p + 3 >= unfilteredBytes.Length) return false;
                        return unfilteredBytes[p] == tcB && unfilteredBytes[p + 1] == tcG && unfilteredBytes[p + 2] == tcR;
                    }

                    int labelTop = sy;
                    while (isLabelColor(sx, labelTop)) labelTop--;
                    labelTop += 2;

                    int labelLeft = sx;
                    while (isLabelColor(labelLeft, labelTop)) labelLeft--;
                    labelLeft += 2;

                    // Find label height from left edge
                    int labelH = 0;
                    while (isLabelColor(labelLeft, labelTop + labelH)) labelH++;
                    labelH -= 2;

                    // Skip checkmark icon, find label width
                    labelLeft = absRightmost;
                    // Check at (Left, Top+Height), then scan width at y=Top
                    int labelW = 0;
                    if (isLabelColor(labelLeft, labelTop + labelH))
                    {
                        labelW++;
                        while (isLabelColor(labelLeft + labelW, labelTop)) labelW++;
                        labelW -= 2;
                    }

                    AppMain.AddLog($"  Count[{ri},{ci}]: label at ({labelLeft},{labelTop}) {labelW}x{labelH}");
                    if (labelW < 5 || labelH < 5) { AppMain.AddLog($"  Count[{ri},{ci}]: label too small"); continue; }

                    var cloneBitmap = CropBitmap(filteredImageClean, labelLeft, labelTop, labelW, labelH);
                    try
                    {
                        using (var pix = SKBitmapToPix(cloneBitmap))
                        using (var page = _tesseractService.NumbersOnlyEngine.Process(pix, PageSegMode.SingleLine))
                        using (var iterator = page.GetIterator())
                        {
                            iterator.Begin();
                            string rawText = iterator.GetText(PageIteratorLevel.TextLine);
                            if (rawText != null) rawText = rawText.Replace(" ", "");
                            AppMain.AddLog($"  Count[{ri},{ci}]: OCR raw=\"{rawText}\"");
                            if (!int.TryParse(rawText, out int itemCount)) itemCount = 1;

                            var itemLabel = new SKRectI(gridCols[ci].Left, gridRows[ri].Top, gridCols[ci].Right, gridRows[ri].Bottom);
                            for (int k = 0; k < foundItems.Count; k++)
                            {
                                var item = foundItems[k];
                                if (item.Bounding.IntersectsWith(itemLabel))
                                {
                                    item.Count = itemCount;
                                    foundItems[k] = item;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"GetItemCounts OCR failed: {ex.Message}");
                    }
                    finally { cloneBitmap.Dispose(); }
                }
            }
        }

        public static void ProcessSnapIt(SKBitmap snapItImage, SKBitmap fullShot, int snapOriginX, int snapOriginY)
        {
            try
            {
            var watch = new Stopwatch();
            watch.Start();
            long start = watch.ElapsedMilliseconds;

            double configScale = ReadUiScaleFromConfig();
            if (configScale > 0)
            {
                uiScaling = configScale;
                AppMain.AddLog($"SnapIt: UI scaling {configScale:P0} from EE.cfg");
            }

            WFtheme theme;
            if (_settings.ThemeSelection != WFtheme.AUTO)
            {
                theme = _settings.ThemeSelection;
            }
            else
            {
                theme = GetThemeWeighted(out _, fullShot);
                if (theme == WFtheme.UNKNOWN)
                {
                    AppMain.AddLog("SnapIt: Theme detection failed");
                    AppMain.StatusUpdate("Snap-It: theme detection failed", 1);
                    return;
                }
            }

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff", AppMain.culture);
            if (_settings.Debug)
                SaveBitmap(snapItImage, Path.Combine(AppMain.AppPath, "debug", "SnapItImage " + timestamp + ".png"));

            using var snapItImageFiltered = ScaleUpAndFilter(snapItImage, theme, out int[] rowHits, out int[] colHits);
            SaveBitmap(snapItImageFiltered, Path.Combine(AppMain.AppPath, "debug", "SnapItImageFiltered " + timestamp + ".png"));

            if (configScale <= 0)
            {
                double detectedScale = DetectUiScale(rowHits, snapItImageFiltered.Width, snapItImageFiltered.Height, fullShot.Height);
                if (detectedScale > 0)
                {
                    uiScaling = detectedScale;
                    AppMain.AddLog($"SnapIt: UI scaling {detectedScale:P0} from row analysis");
                }
            }

            double imageScale = (double)snapItImageFiltered.Height / snapItImage.Height;
            var foundParts = FindAllParts(snapItImageFiltered, snapItImage, rowHits, colHits);

            long end = watch.ElapsedMilliseconds;
            AppMain.StatusUpdate($"Snap-It processing ({end - start}ms)", 0);

            string csv = string.Empty;
            string datestamp = DateTime.UtcNow.ToString("yyyy-MM-dd", AppMain.culture);
            if (!File.Exists(Path.Combine(AppMain.AppPath, "export " + datestamp + ".csv")) && _settings.SnapitExport)
                csv += "ItemName,Plat,Ducats,Volume,Vaulted,Owned,partsDetected" + Environment.NewLine;

            int resultCount = foundParts.Count;
            double screenScale = _window?.ScreenScaling ?? 1.0;

            for (int i = 0; i < foundParts.Count; i++)
            {
                var part = foundParts[i];
                if (!PartNameValid(part.Name))
                {
                    foundParts.RemoveAt(i); i--; resultCount--; continue;
                }

                string name = AppMain.dataBase.GetPartName(part.Name, out int levenDist, false, out bool multipleLowest);
                int ocrWordCount = part.Name.Split(WordSplitChars, StringSplitOptions.RemoveEmptyEntries)
                    .Count(w => w.Length > 1);
                if (ocrWordCount >= 3)
                {
                    string partNameWithBP = part.Name + " Blueprint";
                    string nameWithBP = AppMain.dataBase.GetPartName(partNameWithBP, out int levenDistBP, true, out bool multipleLowestBP);
                    if (levenDistBP <= 3 && levenDistBP < levenDist && !string.IsNullOrEmpty(nameWithBP))
                    {
                        AppMain.AddLog($"  Blueprint fallback: \"{name}\"(d={levenDist}) -> \"{nameWithBP}\"(d={levenDistBP})");
                        name = nameWithBP;
                        levenDist = levenDistBP;
                        multipleLowest = multipleLowestBP;
                    }
                }
                if (levenDist == 9999 || levenDist > GetMaxAllowedLevenshteinDistance(part.Name.Length) || string.IsNullOrEmpty(name))
                {
                    foundParts.RemoveAt(i); i--; resultCount--; continue;
                }

                string primeSetName = Data.GetSetName(name);
                if (levenDist > Math.Min(part.Name.Length, name.Length) / 3 || multipleLowest)
                    part.Warning = true;

                bool doWarn = part.Warning;
                part.Name = name;
                foundParts[i] = part;

                JObject job = AppMain.dataBase.marketData?.GetValue(name) as JObject;
                if (job == null)
                {
                    foundParts.RemoveAt(i); i--; resultCount--; continue;
                }

                JObject primeSet = AppMain.dataBase.marketData?.GetValue(primeSetName) as JObject;
                string plat = job["plat"]?.ToObject<string>() ?? "?";
                string primeSetPlat = primeSet != null ? (string)primeSet["plat"] : null;
                string ducats = job["ducats"]?.ToObject<string>() ?? "?";
                string volume = job["volume"]?.ToObject<string>() ?? "?";
                bool vaulted = SafeCall(() => AppMain.dataBase.IsPartVaulted(name), false, "IsPartVaulted", name);
                bool mastered = SafeCall(() => AppMain.dataBase.IsPartMastered(name), false, "IsPartMastered", name);
                string partsOwned = SafeCall(() => AppMain.dataBase.PartsOwned(name), "0", "PartsOwned", name);
                string partsDetected = "" + part.Count;

                if (_settings.SnapitExport)
                {
                    var owned = string.IsNullOrEmpty(partsOwned) ? "0" : partsOwned;
                    csv += name + "," + plat + "," + ducats + "," + volume + "," + vaulted.ToString(AppMain.culture) + "," + owned + "," + partsDetected + ", \"\"" + Environment.NewLine;
                }

                int origCenterX = (int)((part.Bounding.Left + part.Bounding.Width / 2.0) / imageScale);
                int origY = (int)(part.Bounding.Top / imageScale);
                int origW = (int)(part.Bounding.Width / imageScale);

                int width = (int)(origW * screenScale);
                if (width < _settings.MinOverlayWidth) width = _settings.MinOverlayWidth;
                else if (width > _settings.MaxOverlayWidth) width = _settings.MaxOverlayWidth;

                var wnd = _window?.Window;
                int wndLeft = wnd?.Left ?? 0;
                int wndTop = wnd?.Top ?? 0;
                int overlayX = wndLeft + snapOriginX + origCenterX - width / 2;
                int overlayY = wndTop + snapOriginY + origY - SnapItOverlayHeight;

                OnSnapItRewardDisplay?.Invoke(name, plat, primeSetPlat, ducats, volume, vaulted, mastered,
                    partsOwned, partsDetected, false, doWarn, width, overlayX, overlayY);
            }

            if (_settings.DoSnapItCount && resultCount > 0)
                OnSnapItVerifyCount?.Invoke(foundParts);

            end = watch.ElapsedMilliseconds;
            if (resultCount == 0)
            {
                AppMain.StatusUpdate($"Snap-It: no items found ({end - start}ms)", 1);
                AppMain.SpawnErrorPopup(DateTime.UtcNow);
            }
            else
            {
                AppMain.StatusUpdate($"Snap-It complete: {resultCount} items ({end - start}ms)", 0);
            }
            watch.Stop();
            AppMain.AddLog($"Snap-it finished, items: {resultCount}, time: {end - start}ms");

            if (_settings.SnapitExport && !string.IsNullOrEmpty(csv))
                File.AppendAllText(Path.Combine(AppMain.AppPath, "export " + datestamp + ".csv"), csv);
            }
            finally
            {
                TrimNativeHeap();
            }
        }

        #endregion
    }

    public struct InventoryItem
    {
        public string Name;
        public SKRectI Bounding;
        public int Count;
        public bool Warning;

        public InventoryItem(string itemName, SKRectI boundingBox, bool showWarning = false)
        {
            Name = itemName;
            Bounding = boundingBox;
            Count = 1;
            Warning = showWarning;
        }
    }
}
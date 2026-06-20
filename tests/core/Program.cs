using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SkiaSharp;
using WFInfo;
using WFInfo.LanguageProcessing;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;
using WFInfo.Services.WindowInfo;
using WFInfo.Settings;
using WFInfo.Tests;

/// <summary>
/// Core OCR test runner - same test data as WPF (tests/data/*.png + *.json),
/// but runs through Core's SkiaSharp-based OCR pipeline on Linux.
///
/// Usage: dotnet run --project tests/core -- ../map.json [output.json]
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run --project tests/core -- <map.json> [output.json]");
            Console.WriteLine();
            Console.WriteLine("  map.json    - Test map file listing scenario paths");
            Console.WriteLine("  output.json - (optional) Output results file");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  dotnet run --project tests/core -- tests/map.json results.json");
            return 1;
        }

        string testMapPath = args[0];
        string outputPath = args.Length > 1 ? args[1] : $"test_results_{DateTime.Now:yyyyMMdd_HHmmss}.json";

        Console.WriteLine("═══ WFInfo Core OCR Tests ═══");
        Console.WriteLine();
        Console.WriteLine($"Map:    {Path.GetFullPath(testMapPath)}");
        Console.WriteLine($"Output: {Path.GetFullPath(outputPath)}");
        Console.WriteLine();

        if (!File.Exists(testMapPath))
        {
            Console.Error.WriteLine($"ERROR: map file not found: {testMapPath}");
            return 2;
        }

        try
        {
            // Initialize Core
            AppMain.Initialize();

            var settings = ApplicationSettings.GlobalSettings;
            settings.Debug = true;

            var processFinder = new HeadlessProcessFinder();
            var windowService = new HeadlessWindowInfoService();

            // Initialize Data (downloads market data on first run)
            Console.WriteLine("Initializing databases...");
            AppMain.dataBase = new Data(ApplicationSettings.GlobalReadonlySettings, processFinder, windowService);
            await AppMain.dataBase.Update();
            Console.WriteLine("Databases ready.");

            // Initialize OCR
            var tesseractService = new TesseractService(ApplicationSettings.GlobalReadonlySettings);
            OCR.Init(tesseractService, new SilentSoundPlayer(), ApplicationSettings.GlobalReadonlySettings,
                windowService, new NullScreenshotService());
            Console.WriteLine("OCR engine ready.");
            Console.WriteLine();

            // Run tests
            var runner = new CoreTestRunner(windowService, settings, tesseractService);
            var results = runner.RunTestSuite(testMapPath);

            // Save & report
            var json = JsonConvert.SerializeObject(results, Formatting.Indented);
            File.WriteAllText(outputPath, json);
            PrintSummary(results);

            Console.WriteLine();
            Console.WriteLine($"Results saved to: {Path.GetFullPath(outputPath)}");

            if (!string.IsNullOrEmpty(results.ErrorMessage)) return 2;
            if (results.FailedTests > 0 || results.ErrorTests > 0) return 1;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 2;
        }
    }

    static void PrintSummary(TestSuiteResult results)
    {
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("  TEST RESULTS SUMMARY");
        Console.WriteLine("========================================");
        Console.WriteLine($"  Suite:    {results.TestSuiteName}");
        Console.WriteLine($"  Total:    {results.TotalTests}");
        Console.WriteLine($"  Passed:   {results.PassedTests}");
        Console.WriteLine($"  Failed:   {results.FailedTests}");
        if (results.ErrorTests > 0)
            Console.WriteLine($"  Errors:   {results.ErrorTests}");
        Console.WriteLine($"  Pass Rate: {results.PassRate:F1}%");
        Console.WriteLine($"  Accuracy:  {results.OverallAccuracy:F1}%");
        Console.WriteLine($"  Duration:  {(results.EndTime - results.StartTime).TotalSeconds:F1}s");

        if (results.LanguageCoverage.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  By Language:");
            foreach (var kv in results.LanguageCoverage)
            {
                var c = kv.Value;
                Console.WriteLine($"    {kv.Key,-20} {c.PassedTests}/{c.TotalTests} pass  {c.AverageAccuracy:F0}% acc  {c.AverageProcessingTime:F0}ms avg");
            }
        }

        if (results.CategoryCoverage.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  By Category:");
            foreach (var kv in results.CategoryCoverage)
            {
                var c = kv.Value;
                Console.WriteLine($"    {kv.Key,-20} {c.PassedTests}/{c.TotalTests} pass  {c.AverageAccuracy:F0}% acc  {c.AverageProcessingTime:F0}ms avg");
            }
        }

        var problems = results.TestResults.FindAll(t => !t.Success);
        if (problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Failed/Error Details:");
            foreach (var t in problems)
            {
                if (!string.IsNullOrEmpty(t.ErrorMessage))
                {
                    Console.WriteLine($"    ERROR {t.TestCaseName}: {t.ErrorMessage}");
                }
                else
                {
                    Console.WriteLine($"    FAIL  {t.TestCaseName} ({t.AccuracyScore:F0}% accuracy)");
                    if (t.MissingParts.Count > 0)
                        Console.WriteLine($"          Missing: {string.Join(", ", t.MissingParts)}");
                    if (t.ExtraParts.Count > 0)
                        Console.WriteLine($"          Extra:   {string.Join(", ", t.ExtraParts)}");
                    if (t.ActualParts.Count > 0)
                        Console.WriteLine($"          Got:     {string.Join(", ", t.ActualParts)}");
                }
            }
        }

        Console.WriteLine("========================================");
    }
}

/// <summary>
/// Core test runner - mirrors WPF's OCRTestRunner but uses Core's SkiaSharp pipeline.
/// Captures results via OCR.OnRewardsProcessed event instead of a dedicated ForTest method.
/// </summary>
class CoreTestRunner
{
    private readonly HeadlessWindowInfoService _windowService;
    private readonly ApplicationSettings _settings;
    private readonly TesseractService _tesseractService;
    private string _currentLocale;

    public CoreTestRunner(HeadlessWindowInfoService windowService, ApplicationSettings settings, TesseractService tesseractService)
    {
        _windowService = windowService;
        _settings = settings;
        _tesseractService = tesseractService;
    }

    public TestSuiteResult RunTestSuite(string testMapPath)
    {
        var result = new TestSuiteResult
        {
            TestSuiteName = Path.GetFileNameWithoutExtension(testMapPath),
            StartTime = DateTime.UtcNow
        };

        try
        {
            var testMap = JsonConvert.DeserializeObject<TestMap>(File.ReadAllText(testMapPath));
            if (testMap?.Scenarios == null || testMap.Scenarios.Count == 0)
                throw new InvalidDataException($"No scenarios in: {testMapPath}");

            string testMapDir = Path.GetDirectoryName(Path.GetFullPath(testMapPath));

            Console.WriteLine($"Running {testMap.Scenarios.Count} scenario(s)...");
            Console.WriteLine();

            foreach (var scenario in testMap.Scenarios)
            {
                var testResult = RunSingleTest(scenario, testMapDir);
                result.TestResults.Add(testResult);
            }

            CalculateStatistics(result);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Console.Error.WriteLine($"Suite error: {ex.Message}");
        }

        result.EndTime = DateTime.UtcNow;
        return result;
    }

    private TestResult RunSingleTest(string scenarioPath, string testMapDir)
    {
        var stopwatch = Stopwatch.StartNew();
        string jsonPath = Path.GetFullPath(Path.Combine(testMapDir, scenarioPath + ".json"));
        string imagePath = Path.GetFullPath(Path.Combine(testMapDir, scenarioPath + ".png"));
        string testName = Path.GetFileName(scenarioPath);

        var result = new TestResult { TestCaseName = testName, ImagePath = imagePath };

        try
        {
            if (!File.Exists(jsonPath)) { result.ErrorMessage = $"JSON not found: {jsonPath}"; result.Success = false; return result; }
            if (!File.Exists(imagePath)) { result.ErrorMessage = $"PNG not found: {imagePath}"; result.Success = false; return result; }

            var testCase = JsonConvert.DeserializeObject<TestCase>(File.ReadAllText(jsonPath));
            if (testCase == null) { result.ErrorMessage = "Failed to parse test JSON"; result.Success = false; return result; }

            result.Language = testCase.Language ?? "unknown";
            result.Theme = testCase.Theme ?? "auto";
            result.Category = testCase.Category ?? "reward";
            result.ExpectedParts = testCase.Parts?.Values.ToList() ?? new List<string>();

            Console.Write($"  {testName,-20} [{result.Language}/{result.Category}] ");

            // Configure settings for this test
            ApplyTestSettings(testCase);

            // Load image as SKBitmap
            using var bitmap = SKBitmap.Decode(imagePath);
            if (bitmap == null) { result.ErrorMessage = "Failed to decode PNG"; result.Success = false; return result; }

            // Tell window service about the image dimensions
            _windowService.UseImage(bitmap);

            // Capture OCR results via event
            List<string> capturedRewards = null;
            List<string> capturedNames = new List<string>();

            void rewardsHandler(List<string> rewards) { capturedRewards = new List<string>(rewards); }
            void displayHandler(int idx, string name, string plat, string setPlat, string ducats,
                string volume, bool vaulted, bool mastered, string owned, string extra, bool hide, bool b2, string highlight)
            { capturedNames.Add(name); }

            OCR.OnRewardsProcessed += rewardsHandler;
            OCR.OnRewardDisplay += displayHandler;

            try
            {
                // Run the real Core OCR pipeline
                OCR.ProcessRewardScreen(bitmap);
            }
            finally
            {
                OCR.OnRewardsProcessed -= rewardsHandler;
                OCR.OnRewardDisplay -= displayHandler;
            }

            // Use whichever source got results
            result.ActualParts = capturedRewards ?? capturedNames;

            CompareResults(result);

            stopwatch.Stop();
            result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            string status = result.Success ? "PASS" : "FAIL";
            Console.WriteLine($"{status} ({result.AccuracyScore:F0}% accuracy, {result.ProcessingTimeMs}ms)");
            if (!result.Success)
            {
                if (result.MissingParts.Count > 0) Console.WriteLine($"    Missing: {string.Join(", ", result.MissingParts)}");
                if (result.ExtraParts.Count > 0) Console.WriteLine($"    Extra:   {string.Join(", ", result.ExtraParts)}");
                Console.WriteLine($"    Got:     {string.Join(", ", result.ActualParts)}");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;
            result.ErrorMessage = ex.Message;
            result.Success = false;
            Console.WriteLine($"ERROR: {ex.Message}");
        }

        return result;
    }

    private void ApplyTestSettings(TestCase testCase)
    {
        string newLocale = MapLanguageToLocale(testCase.Language);
        bool localeChanged = newLocale != _currentLocale;
        _settings.Locale = newLocale;
        _currentLocale = newLocale;

        _settings.ThemeSelection = MapThemeToEnum(testCase.Theme);

        if (testCase.Scaling > 0)
            OCR.uiScaling = testCase.Scaling / 100.0;

        if (localeChanged)
        {
            Console.Write($"(reloading engines for '{newLocale}') ");
            _tesseractService.ReloadEngines();
            LanguageProcessorFactory.Initialize(ApplicationSettings.GlobalReadonlySettings);
            AppMain.dataBase.ReloadItems().GetAwaiter().GetResult();
        }
    }

    private static string MapLanguageToLocale(string language)
    {
        if (string.IsNullOrEmpty(language)) return "en";
        return language.ToLower() switch
        {
            "english" => "en", "korean" => "ko", "japanese" => "ja",
            "simplified chinese" => "zh-hans", "traditional chinese" => "zh-hant",
            "thai" => "th", "french" => "fr", "ukrainian" => "uk",
            "italian" => "it", "german" => "de", "spanish" => "es",
            "portuguese" => "pt", "polish" => "pl", "turkish" => "tr",
            "russian" => "ru", _ => "en"
        };
    }

    private static WFtheme MapThemeToEnum(string theme)
    {
        if (string.IsNullOrEmpty(theme)) return WFtheme.AUTO;
        return theme.ToLower() switch
        {
            "orokin" => WFtheme.OROKIN, "tenno" => WFtheme.TENNO,
            "grineer" => WFtheme.GRINEER, "corpus" => WFtheme.CORPUS,
            "infested" => WFtheme.NIDUS, "lotus" => WFtheme.LOTUS,
            "fortuna" => WFtheme.FORTUNA, "baruuk" => WFtheme.BARUUK,
            "equinox" => WFtheme.EQUINOX, "dark lotus" or "dark_lotus" => WFtheme.DARK_LOTUS,
            "zephyr" => WFtheme.ZEPHYR, "high contrast" or "high_contrast" => WFtheme.HIGH_CONTRAST,
            "legacy" => WFtheme.LEGACY,
            "vitruvian" => WFtheme.VITRUVIAN, "stalker" => WFtheme.STALKER,
            "conquera" => WFtheme.CONQUERA, "deadlock" => WFtheme.DEADLOCK,
            "lunar renewal" or "lunar_renewal" => WFtheme.LUNAR_RENEWAL,
            "pom 2" or "pom_2" => WFtheme.POM_2,
            _ => WFtheme.AUTO
        };
    }

    private static void CompareResults(TestResult result)
    {
        var expectedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actualCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in result.ExpectedParts)
            expectedCounts[exp] = expectedCounts.TryGetValue(exp, out int c) ? c + 1 : 1;
        foreach (var act in result.ActualParts)
            actualCounts[act] = actualCounts.TryGetValue(act, out int c) ? c + 1 : 1;

        foreach (var kvp in expectedCounts)
        {
            int actual = actualCounts.TryGetValue(kvp.Key, out int c) ? c : 0;
            for (int i = 0; i < kvp.Value - actual; i++)
                result.MissingParts.Add(kvp.Key);
        }

        foreach (var kvp in actualCounts)
        {
            int expected = expectedCounts.TryGetValue(kvp.Key, out int c) ? c : 0;
            for (int i = 0; i < kvp.Value - expected; i++)
                result.ExtraParts.Add(kvp.Key);
        }

        int totalExpected = result.ExpectedParts.Count;
        int matched = 0;
        foreach (var kvp in expectedCounts)
        {
            int actual = actualCounts.TryGetValue(kvp.Key, out int c) ? c : 0;
            matched += Math.Min(kvp.Value, actual);
        }

        result.AccuracyScore = totalExpected > 0 ? (double)matched / totalExpected * 100.0 : 0;
        result.Success = result.MissingParts.Count == 0 && result.ExtraParts.Count == 0 && string.IsNullOrEmpty(result.ErrorMessage);
    }

    private static void CalculateStatistics(TestSuiteResult suite)
    {
        suite.TotalTests = suite.TestResults.Count;
        suite.PassedTests = suite.TestResults.Count(t => t.Success);
        suite.FailedTests = suite.TestResults.Count(t => !t.Success && t.ErrorMessage == null);
        suite.ErrorTests = suite.TestResults.Count(t => t.ErrorMessage != null && !t.Success);
        suite.OverallAccuracy = suite.TestResults.Count > 0 ? suite.TestResults.Average(t => t.AccuracyScore) : 0;
        suite.PassRate = suite.TotalTests > 0 ? (double)suite.PassedTests / suite.TotalTests * 100 : 0;

        foreach (var group in suite.TestResults.GroupBy(t => t.Category ?? "unknown"))
            suite.CategoryCoverage[group.Key] = BuildCoverage(group);
        foreach (var group in suite.TestResults.GroupBy(t => t.Language ?? "unknown"))
            suite.LanguageCoverage[group.Key] = BuildCoverage(group);

        suite.OverallCoverage = new TestCoverage
        {
            TotalTests = suite.TotalTests, PassedTests = suite.PassedTests, FailedTests = suite.FailedTests,
            PassRate = suite.PassRate, AverageAccuracy = suite.OverallAccuracy,
            AverageProcessingTime = suite.TestResults.Count > 0 ? suite.TestResults.Average(t => t.ProcessingTimeMs) : 0
        };
    }

    private static TestCoverage BuildCoverage(IGrouping<string, TestResult> group)
    {
        return new TestCoverage
        {
            TotalTests = group.Count(), PassedTests = group.Count(t => t.Success),
            FailedTests = group.Count(t => !t.Success),
            PassRate = group.Count() > 0 ? (double)group.Count(t => t.Success) / group.Count() * 100 : 0,
            AverageAccuracy = group.Average(t => t.AccuracyScore),
            AverageProcessingTime = group.Average(t => t.ProcessingTimeMs)
        };
    }
}

// ── Headless service stubs ──

class HeadlessProcessFinder : IProcessFinder
{
    public Process Warframe => null;
    public bool IsRunning => false;
    public WineEnvironmentInfo WineEnvironment => null;
    public event ProcessChangedArgs OnProcessChanged { add { } remove { } }
}

class HeadlessWindowInfoService : IWindowInfoService
{
    private SKRectI _window;
    private SKPointI _center;
    public double ScreenScaling => _window.Height / 1080.0;
    public SKRectI Window => _window;
    public SKPointI Center => _center;
    public SKRectI ScreenBounds => _window;

    public void UpdateWindow() { }

    public void UseImage(SKBitmap bitmap)
    {
        _window = new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        _center = new SKPointI(bitmap.Width / 2, bitmap.Height / 2);
    }
}

class SilentSoundPlayer : ISoundPlayer
{
    public void Play() { }
}

class NullScreenshotService : WFInfo.Services.Screenshot.IScreenshotService
{
    public bool IsAvailable => false;
    public Task<List<SKBitmap>> CaptureScreenshot() => Task.FromResult(new List<SKBitmap>());
}

// ── Test models (ported from WPF WFInfo.Tests.TestModels) ──

namespace WFInfo.Tests
{
    class TestCase
    {
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("resolution")] public string Resolution { get; set; }
        [JsonProperty("scaling")] public int Scaling { get; set; }
        [JsonProperty("theme")] public string Theme { get; set; }
        [JsonProperty("language")] public string Language { get; set; }
        [JsonProperty("parts")] public Dictionary<string, string> Parts { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("hdr")] public bool HDR { get; set; }
        [JsonProperty("filters")] public List<string> Filters { get; set; }
    }

    class TestMap
    {
        [JsonProperty("scenarios")] public List<string> Scenarios { get; set; } = new List<string>();
    }

    class TestResult
    {
        public string TestCaseName { get; set; }
        public string ImagePath { get; set; }
        public string Language { get; set; }
        public string Theme { get; set; }
        public string Category { get; set; }
        public bool Success { get; set; }
        public List<string> ExpectedParts { get; set; } = new List<string>();
        public List<string> ActualParts { get; set; } = new List<string>();
        public List<string> MissingParts { get; set; } = new List<string>();
        public List<string> ExtraParts { get; set; } = new List<string>();
        public double AccuracyScore { get; set; }
        public long ProcessingTimeMs { get; set; }
        public string ErrorMessage { get; set; }
    }

    class TestSuiteResult
    {
        public string TestSuiteName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<TestResult> TestResults { get; set; } = new List<TestResult>();
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public int ErrorTests { get; set; }
        public double OverallAccuracy { get; set; }
        public double PassRate { get; set; }
        public Dictionary<string, TestCoverage> CategoryCoverage { get; set; } = new Dictionary<string, TestCoverage>();
        public Dictionary<string, TestCoverage> LanguageCoverage { get; set; } = new Dictionary<string, TestCoverage>();
        public TestCoverage OverallCoverage { get; set; }
        public string ErrorMessage { get; set; }
    }

    class TestCoverage
    {
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public double PassRate { get; set; }
        public double AverageAccuracy { get; set; }
        public double AverageProcessingTime { get; set; }
    }
}
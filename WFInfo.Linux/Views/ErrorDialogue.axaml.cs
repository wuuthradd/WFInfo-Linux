using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WFInfo.Services;

namespace WFInfo.Linux.Views
{
    public partial class ErrorDialogue : Window
    {
        private readonly string _appPath = PlatformPaths.AppDataPath;
        private readonly string _debugDir;
        private readonly string _zipDir;

        private readonly int _distance;
        private readonly DateTime _closest;

        public ErrorDialogue()
        {
            InitializeComponent();
        }

        public ErrorDialogue(DateTime timeStamp, int gap = 30) : this()
        {
            _distance = gap;
            _closest = timeStamp;
            _debugDir = Path.Combine(_appPath, "debug");
            _zipDir = Path.Combine(_appPath, "generatedZip");
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                try { BeginMoveDrag(e); } catch (InvalidOperationException) { }
        }

        private void YesClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_zipDir);

                var fullZipPath = Path.Combine(_zipDir, $"WFInfoError_{_closest:yyyy-MM-dd_HH-mm-ssff}.zip");

                using (var zip = ZipFile.Open(fullZipPath, ZipArchiveMode.Create))
                {
                    AddFileIfExists(zip, Path.Combine(_appPath, "debug.log"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "settings.json"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "vklayer.log"));

                    AddFileIfExists(zip, Path.Combine(_appPath, "eqmt_data.json"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "market_data.json"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "market_items.json"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "name_data.json"));
                    AddFileIfExists(zip, Path.Combine(_appPath, "relic_data.json"));

                    // Debug folder screenshots near the timestamp (most recent 100)
                    if (Directory.Exists(_debugDir))
                    {
                        var files = new DirectoryInfo(_debugDir).GetFiles()
                            .Where(f => f.CreationTimeUtc > _closest.AddSeconds(-_distance))
                            .Where(f => f.CreationTimeUtc < _closest.AddSeconds(_distance))
                            .OrderByDescending(f => f.CreationTimeUtc)
                            .Take(100);

                        foreach (var file in files)
                        {
                            try { zip.CreateEntryFromFile(file.FullName, "debug/" + file.Name); }
                            catch { /* skip locked files */ }
                        }
                    }
                }

                try { Process.Start(new ProcessStartInfo(_zipDir) { UseShellExecute = true }); }
                catch { /* non-critical */ }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Unable to zip due to: {ex}");
            }

            Close();
        }

        private static void AddFileIfExists(ZipArchive zip, string path)
        {
            if (File.Exists(path))
            {
                try { zip.CreateEntryFromFile(path, Path.GetFileName(path)); }
                catch { /* skip locked files */ }
            }
        }

        private void NoClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
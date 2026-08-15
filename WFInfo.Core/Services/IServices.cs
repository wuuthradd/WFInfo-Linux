using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SkiaSharp;
using Tesseract;
using WFInfo.Models;

namespace WFInfo.Services
{
    public interface ILogger
    {
        void AddLog(string message);
    }

    public interface ISoundPlayer
    {
        void Play();
    }

    public interface ITesseractService
    {
        TesseractEngine FirstEngine { get; }
        TesseractEngine[] Engines { get; }
        TesseractEngine NumbersOnlyEngine { get; }
        void Init();
        void ReloadEngines();
    }

    public delegate void LogWatcherEventHandler(object sender, string text);

    public interface ILogCapture : IDisposable
    {
        event LogWatcherEventHandler TextChanged;
    }

    public class KeyEventArgs : EventArgs
    {
        public VirtualKey Key { get; set; }
        public bool IsDown { get; set; }
    }

    public class MouseEventArgs : EventArgs
    {
        public VirtualMouseButton Button { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public interface IInputListener : IDisposable
    {
        event EventHandler<KeyEventArgs> KeyEvent;
        event EventHandler<MouseEventArgs> MouseEvent;
        bool IsKeyHeld(VirtualKey key) => false;
        string StartupWarning => null;
    }
}

namespace WFInfo.Services.Screenshot
{
    public interface IScreenshotService
    {
        Task<List<SKBitmap>> CaptureScreenshot();
        bool IsAvailable { get; }
    }
}

namespace WFInfo.Services.WindowInfo
{
    public interface IWindowInfoService
    {
        double ScreenScaling { get; }
        SKRectI Window { get; }
        SKPointI Center { get; }
        SKRectI ScreenBounds { get; }
        void UpdateWindow();
        void UseImage(SKBitmap bitmap);
    }
}

namespace WFInfo.Services.WarframeProcess
{
    public delegate void ProcessChangedArgs(Process newProcess);

    public interface IProcessFinder
    {
        Process Warframe { get; }
        bool IsRunning { get; }
        WineEnvironmentInfo WineEnvironment { get; }
        event ProcessChangedArgs OnProcessChanged;
    }

    public class WineEnvironmentInfo
    {
        public string WinePrefix { get; set; }
        public string CompatDataPath { get; set; }
        public string WineLoaderPath { get; set; }
        public string CompatToolPaths { get; set; }
        public string ProtonVersion { get; set; }

        public string EELogPath
        {
            get
            {
                string prefix = WinePrefix ?? (CompatDataPath != null ? System.IO.Path.Combine(CompatDataPath, "pfx") : null);
                if (prefix == null) return null;
                string steamPath = System.IO.Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Warframe", "EE.log");
                if (System.IO.File.Exists(steamPath))
                    return steamPath;
                string realPath = System.IO.Path.Combine(prefix, "drive_c", "users", System.Environment.UserName, "AppData", "Local", "Warframe", "EE.log");
                if (System.IO.File.Exists(realPath))
                    return realPath;
                return steamPath;
            }
        }
    }
}

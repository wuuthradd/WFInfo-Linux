using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using WFInfo.Services;

namespace WFInfo.Linux
{
    internal class Program
    {
        private static FileStream _lockFile;

        public static void ReleaseLock()
        {
            _lockFile?.Dispose();
            _lockFile = null;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--setup-input")
            {
                string script = Path.Combine(AppContext.BaseDirectory, "wfinfo-setup-input.sh");
                if (!File.Exists(script))
                {
                    Console.Error.WriteLine($"Setup script not found: {script}");
                    Environment.Exit(1);
                }
                var psi = new System.Diagnostics.ProcessStartInfo("bash", script)
                {
                    UseShellExecute = false
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                Environment.Exit(proc?.ExitCode ?? 1);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    AppMain.AddLog($"FATAL unhandled exception: {ex}");
                    AppMain.FlushLog();
                }
                catch { /* last resort - nothing we can do */ }
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                AppMain.AddLog($"Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            OCR.LimitMallocArenas();

            string lockPath = Path.Combine(PlatformPaths.AppDataPath, "wfinfo.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
            try
            {
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Another instance of WFInfo is already running.");
                Environment.Exit(1);
                return;
            }

            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => App.PerformCleanup());
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => App.PerformCleanup());

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                _lockFile?.Dispose();
                try { File.Delete(lockPath); } catch { }
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .With(new X11PlatformOptions { OverlayPopups = true })
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WFInfo.Services;
using WFInfo.Settings;

namespace WFInfo
{
    /// <summary>
    /// Cross-platform static coordinator.
    /// Provides logging, status updates, and holds shared references.
    /// </summary>
    public static class AppMain
    {
        public static string AppPath => PlatformPaths.AppDataPath;
        public static string buildVersion = "1.0.0";
        public static Data dataBase;

        public static CultureInfo culture = new CultureInfo("en", false);

        // UI update events
        public static event Action<string, int> OnStatusUpdate;
        public static event Action<Action> OnRunOnUIThread;
        public static event Action<DateTime, int> OnSpawnErrorPopup;

        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static readonly Timer _logFlushTimer = new Timer(FlushLogQueue, null, 250, 250);
        private static int _isFlushing = 0;
        private static int _shutdownInProgress = 0;
        private static readonly object _logFileWriteLock = new object();
        private static int _consecutiveFlushFailures = 0;
        private const int _flushRetryLimit = 5;

        public static string BuildVersion { get => buildVersion; }

        public static void Initialize()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    string ver = asm.GetName().Version?.ToString() ?? "1.0.0";
                    if (ver.EndsWith(".0"))
                        ver = ver.Substring(0, ver.LastIndexOf('.'));
                    buildVersion = ver;
                }
            }
            catch { }

            Directory.CreateDirectory(AppPath);
            Directory.CreateDirectory(Path.Combine(AppPath, "debug"));

            try
            {
                string logPath = Path.Combine(AppPath, "debug.log");
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 100 * 1024 * 1024)
                {
                    string oldPath = logPath + ".old";
                    File.Delete(oldPath);
                    File.Move(logPath, oldPath);
                }
            }
            catch { }

            lock (_logFileWriteLock)
            {
                using (StreamWriter sw = File.AppendText(Path.Combine(AppPath, "debug.log")))
                {
                    sw.WriteLine("--------------------------------------------------------------------------------------------------------------------------------------------");
                    sw.WriteLine("   STARTING WFINFO " + buildVersion + " at " + DateTime.UtcNow);
                    sw.WriteLine("--------------------------------------------------------------------------------------------------------------------------------------------");
                }
            }
        }

        public static void AddLog(string argm)
        {
            Debug.WriteLine(argm);
            Console.WriteLine(argm);
            string logEntry = "[" + DateTime.UtcNow + " " + buildVersion + "]   " + argm;

            // During shutdown, write directly to avoid losing entries
            if (Interlocked.CompareExchange(ref _shutdownInProgress, 0, 0) == 1)
            {
                lock (_logFileWriteLock)
                {
                    try
                    {
                        Directory.CreateDirectory(AppPath);
                        using (StreamWriter sw = File.AppendText(Path.Combine(AppPath, "debug.log")))
                            sw.WriteLine(logEntry);
                    }
                    catch { }
                }
            }
            else
            {
                _logQueue.Enqueue(logEntry);
            }
        }

        private static void FlushLogQueue(object state)
        {
            if (_logQueue.IsEmpty) return;
            if (Interlocked.Exchange(ref _isFlushing, 1) != 0) return;

            var tempList = new List<string>();
            while (_logQueue.TryDequeue(out string line))
                tempList.Add(line);

            if (tempList.Count == 0) { Interlocked.Exchange(ref _isFlushing, 0); return; }

            lock (_logFileWriteLock)
            {
                try
                {
                    Directory.CreateDirectory(AppPath);
                    using (StreamWriter sw = File.AppendText(Path.Combine(AppPath, "debug.log")))
                        foreach (string line in tempList)
                            sw.WriteLine(line);
                    Interlocked.Exchange(ref _consecutiveFlushFailures, 0);
                }
                catch
                {
                    int failures = Interlocked.Increment(ref _consecutiveFlushFailures);
                    if (failures < _flushRetryLimit)
                        foreach (string line in tempList)
                            _logQueue.Enqueue(line);
                }
            }
            Interlocked.Exchange(ref _isFlushing, 0);
        }

        public static void FlushLog()
        {
            Interlocked.Exchange(ref _shutdownInProgress, 1);
            _logFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            // Drain log queue before exit
            for (int i = 0; i < 3; i++)
            {
                FlushLogQueue(null);
                SpinWait.SpinUntil(() => _isFlushing == 0);
                if (_logQueue.IsEmpty) break;
                Thread.Sleep(50);
            }
        }

        public static void StatusUpdate(string message, int severity)
        {
            OnStatusUpdate?.Invoke(message, severity);
        }

        public static void RunOnUIThread(Action act)
        {
            var handler = OnRunOnUIThread;
            if (handler != null)
                handler(act);
            else
                act();
        }

        public static void SpawnErrorPopup(DateTime timeStamp, int gap = 30)
        {
            OnSpawnErrorPopup?.Invoke(timeStamp, gap);
        }
    }
}
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// DI wrapper around AppMain.AddLog, all file I/O is handled by AppMain's
    /// single queue/timer/lock so there's no dual-write to debug.log.
    /// </summary>
    public class SimpleLogger : ILogger
    {
        public void AddLog(string message)
        {
            AppMain.AddLog(message);
        }
    }
}
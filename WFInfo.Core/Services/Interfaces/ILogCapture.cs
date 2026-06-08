using System;

namespace WFInfo.Services
{
    public delegate void LogWatcherEventHandler(object sender, string text);

    /// <summary>
    /// Captures Warframe debug log output (cross-platform).
    /// Windows: OutputDebugString / DBWIN_BUFFER
    /// Linux: tail EE.log from Proton prefix
    /// </summary>
    public interface ILogCapture : IDisposable
    {
        event LogWatcherEventHandler TextChanged;
    }
}
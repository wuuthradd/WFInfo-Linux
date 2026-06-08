namespace WFInfo.Services
{
    /// <summary>
    /// Simple logging abstraction (replaces Main.AddLog static calls).
    /// </summary>
    public interface ILogger
    {
        void AddLog(string message);
    }
}
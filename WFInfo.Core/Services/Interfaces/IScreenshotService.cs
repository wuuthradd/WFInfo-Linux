using System.Collections.Generic;
using System.Threading.Tasks;
using SkiaSharp;

namespace WFInfo.Services.Screenshot
{
    /// <summary>
    /// Provides game screenshots (cross-platform).
    /// </summary>
    public interface IScreenshotService
    {
        /// <summary>
        /// Captures one or more screenshots of the game. All screenshots are in SDR.
        /// </summary>
        Task<List<SKBitmap>> CaptureScreenshot();

        bool IsAvailable { get; }
    }
}
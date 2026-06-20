using SkiaSharp;

namespace WFInfo.Services.WindowInfo
{
    /// <summary>
    /// Provides information about the game window and screen (cross-platform).
    /// </summary>
    public interface IWindowInfoService
    {
        /// <summary>
        /// Gets the screen resolution scaling factor.
        /// </summary>
        double ScreenScaling { get; }

        /// <summary>
        /// Gets the game window rectangle (x, y, width, height) excluding borders.
        /// </summary>
        SKRectI Window { get; }

        /// <summary>
        /// Gets the center of the game window.
        /// </summary>
        SKPointI Center { get; }

        /// <summary>
        /// Gets the screen bounds containing the game window.
        /// </summary>
        SKRectI ScreenBounds { get; }

        /// <summary>
        /// Updates all cached info about the window.
        /// </summary>
        void UpdateWindow();

        /// <summary>
        /// Uses a bitmap to set window info (for testing/debug).
        /// </summary>
        void UseImage(SKBitmap bitmap);
    }
}
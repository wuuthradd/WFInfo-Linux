using System;
using SkiaSharp;
using WFInfo.Services;
using WFInfo.Services.WindowInfo;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Provides game window information from the Vulkan layer's swapchain.
    /// All coordinates are physical pixels (no desktop DPI scaling).
    /// ScreenScaling is the ratio of game height to 1080p.
    /// </summary>
    public class LinuxWindowInfoService : IWindowInfoService
    {
        private readonly VulkanLayerService _vkLayer;
        private readonly ILogger _logger;
        private SKRectI _window;
        private SKPointI _center;
        private SKRectI _screenBounds;
        private double _screenScaling = 1.0;

        public double ScreenScaling => _screenScaling;
        public SKRectI Window => _window;
        public SKPointI Center => _center;
        public SKRectI ScreenBounds => _screenBounds;

        public LinuxWindowInfoService(VulkanLayerService vkLayer, ILogger logger)
        {
            _vkLayer = vkLayer;
            _logger = logger;
        }

        public void UpdateWindow()
        {
            var (w, h) = _vkLayer.QueryWindowInfo();
            if (w > 0 && h > 0)
            {
                _window = new SKRectI(0, 0, w, h);
                _center = new SKPointI(w / 2, h / 2);
                _screenScaling = h / 1080.0; // scale relative to 1080p reference resolution
                _screenBounds = _window;
                return;
            }

            // Layer not connected yet, keep previous values
            if (_window.Width > 0)
                return;

            _logger.AddLog("LinuxWindowInfo: Vulkan layer not available, using defaults");
        }

        public void UseImage(SKBitmap bitmap)
        {
            int w = bitmap?.Width ?? 1920;
            int h = bitmap?.Height ?? 1080;
            if (w <= 0) w = 1920;
            if (h <= 0) h = 1080;

            _window = new SKRectI(0, 0, w, h);
            _center = new SKPointI(w / 2, h / 2);
            _screenScaling = h / 1080.0;
            _screenBounds = _window;
        }
    }
}
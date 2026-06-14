using System;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;
using WFInfo.Services.WindowInfo;
using static WFInfo.Linux.Services.X11Interop;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Provides game window information on Linux via X11 P/Invoke.
    /// No external tool dependencies (xdotool, xrandr, xdpyinfo, xrdb removed).
    /// </summary>
    public class LinuxWindowInfoService : IWindowInfoService
    {
        private readonly IProcessFinder _processFinder;
        private readonly ILogger _logger;
        private SKRectI _window;
        private SKPointI _center;
        private SKRectI _screenBounds;
        private double _dpiScaling = 1.0;
        private double _screenScaling = 1.0;
        private bool _dpiDetected;

        public double DpiScaling => _dpiScaling;
        public double ScreenScaling => _screenScaling;
        public SKRectI Window => _window;
        public SKPointI Center => _center;
        public SKRectI ScreenBounds => _screenBounds;

        public LinuxWindowInfoService(IProcessFinder processFinder, ILogger logger)
        {
            _processFinder = processFinder;
            _logger = logger;
            DetectDpi();
        }

        public void UpdateWindow()
        {
            long xid = _processFinder.WindowId;

            if (xid > 0)
            {
                if (_processFinder is LinuxProcessFinder lpf
                    && lpf.GetWindowGeometry(xid, out int gx, out int gy, out int gw, out int gh))
                {
                    _window = new SKRectI(gx, gy, gx + gw, gy + gh);
                    _center = new SKPointI(gx + gw / 2, gy + gh / 2);
                    _screenScaling = gh / 1080.0;
                    DetectScreenBounds();
                    DetectDpi();
                    return;
                }
            }

            // Fallback: no XID or X11 query failed. Warframe runs fullscreen,
            // so use screen resolution as window bounds.
            if (_window.Width == 0 || _window.Height == 0)
            {
                DetectScreenBounds();
                if (_screenBounds.Width > 0 && _screenBounds.Height > 0)
                {
                    _window = _screenBounds;
                    _center = new SKPointI(_screenBounds.MidX, _screenBounds.MidY);
                    _screenScaling = _screenBounds.Height / 1080.0;
                    _logger.AddLog($"LinuxWindowInfo: Using screen bounds as window (fullscreen): {_screenBounds.Width}x{_screenBounds.Height}, scaling={_screenScaling:F2}");
                }
            }

            DetectDpi();
        }

        public void UseImage(SKBitmap bitmap)
        {
            int w = bitmap?.Width ?? _screenBounds.Width;
            int h = bitmap?.Height ?? _screenBounds.Height;
            if (w <= 0) w = 1920;
            if (h <= 0) h = 1080;

            _window = new SKRectI(0, 0, w, h);
            _center = new SKPointI(w / 2, h / 2);
            _screenScaling = h / 1080.0;
            _screenBounds = _window;
        }

        private void DetectDpi()
        {
            if (_dpiDetected) return;

            // 1. Xft.dpi from X resource database - most reliable on KDE + GNOME
            if (TryGetXftDpi(out double xftScale))
            {
                _dpiScaling = xftScale;
                _dpiDetected = true;
                _logger.AddLog($"LinuxWindowInfo: DPI from Xft.dpi: {_dpiScaling:F2}");
                return;
            }

            // 2. Common scaling environment variables
            string[] scaleVars = { "GDK_SCALE", "QT_SCALE_FACTOR" };
            foreach (string varName in scaleVars)
            {
                string val = Environment.GetEnvironmentVariable(varName);
                if (!string.IsNullOrEmpty(val)
                    && double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double s)
                    && s > 0)
                {
                    _dpiScaling = s;
                    _dpiDetected = true;
                    _logger.AddLog($"LinuxWindowInfo: DPI from {varName}: {_dpiScaling:F2}");
                    return;
                }
            }

            // 3. QT_SCREEN_SCALE_FACTORS (KDE Plasma format: "name=factor;...")
            string qtFactors = Environment.GetEnvironmentVariable("QT_SCREEN_SCALE_FACTORS");
            if (!string.IsNullOrEmpty(qtFactors))
            {
                foreach (string part in qtFactors.Split(';'))
                {
                    string factor = part.Contains('=') ? part.Split('=')[1] : part;
                    if (double.TryParse(factor.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double f) && f > 0)
                    {
                        _dpiScaling = f;
                        _dpiDetected = true;
                        _logger.AddLog($"LinuxWindowInfo: DPI from QT_SCREEN_SCALE_FACTORS: {_dpiScaling:F2}");
                        return;
                    }
                }
            }

            // GDK_DPI_SCALE is a multiplier on top of GDK_SCALE, not an absolute
            // scale factor (e.g., GDK_SCALE=2 + GDK_DPI_SCALE=0.5 = effective 1.0).
            // Intentionally not used standalone.
        }

        private bool TryGetXftDpi(out double scale)
        {
            scale = 1.0;
            try
            {
                IntPtr display = SharedDisplay;
                if (display == IntPtr.Zero) return false;

                IntPtr rms = XResourceManagerString(display);
                if (rms == IntPtr.Zero) return false;

                string resources = Marshal.PtrToStringAnsi(rms);
                if (string.IsNullOrEmpty(resources)) return false;

                foreach (string line in resources.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("Xft.dpi:", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                        if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double dpi)
                            && dpi > 0)
                        {
                            scale = dpi / 96.0;
                            return scale > 0.5 && scale <= 8.0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void DetectScreenBounds()
        {
            try
            {
                IntPtr display = SharedDisplay;
                if (display == IntPtr.Zero) return;

                var (mx, my, mw, mh) = GetMonitorAtPoint(display, _center.X, _center.Y);
                if (mw > 0 && mh > 0)
                    _screenBounds = new SKRectI(mx, my, mx + mw, my + mh);
            }
            catch (Exception ex)
            {
                _logger.AddLog($"LinuxWindowInfo: Screen detection error: {ex.Message}");
            }
        }
    }
}
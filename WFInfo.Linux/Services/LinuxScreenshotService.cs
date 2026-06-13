using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SkiaSharp;
using WFInfo.Services;
using WFInfo.Services.Screenshot;
using WFInfo.Services.WarframeProcess;
using WFInfo.Services.WindowInfo;
using static WFInfo.Linux.Services.X11Interop;

namespace WFInfo.Linux.Services
{
    public class LinuxScreenshotService : IScreenshotService
    {
        private readonly IProcessFinder _processFinder;
        private readonly IWindowInfoService _windowInfo;
        private readonly ILogger _logger;

        private readonly object _captureLock = new object();

        private bool _shmProbed;
        private bool _shmAvailable;
        private bool _rootFallbackLogged;

        private XShmSegmentInfo _shmInfo;
        private IntPtr _shmImage;
        private int _shmWidth;
        private int _shmHeight;

        public bool IsAvailable => true;

        public LinuxScreenshotService(IProcessFinder processFinder, IWindowInfoService windowInfo, ILogger logger)
        {
            _processFinder = processFinder;
            _windowInfo = windowInfo;
            _logger = logger;
        }

        public Task<List<SKBitmap>> CaptureScreenshot()
        {
            var bitmaps = new List<SKBitmap>();

            try
            {
                var x11Bitmap = CaptureViaX11();
                if (x11Bitmap != null)
                {
                    _logger.AddLog($"X11 screenshot: {x11Bitmap.Width}x{x11Bitmap.Height}");
                    bitmaps.Add(x11Bitmap);
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"Screenshot error: {ex.Message}");
            }

            return Task.FromResult(bitmaps);
        }

        private void ProbeShm(IntPtr display)
        {
            if (_shmProbed) return;
            _shmProbed = true;

            if (X11Interop.IsXWayland)
            {
                _shmAvailable = false;
                _logger.AddLog("X11: XShm disabled on XWayland (unreliable)");
                return;
            }

            try
            {
                _shmAvailable = XShmQueryExtension(display) != 0;
            }
            catch (DllNotFoundException)
            {
                _shmAvailable = false;
            }

            _logger.AddLog($"X11: XShm {(_shmAvailable ? "available" : "not available")}");
        }

        private void DestroyShmSegment(IntPtr display)
        {
            if (_shmImage != IntPtr.Zero)
            {
                XShmDetach(display, ref _shmInfo);
                XDestroyImage(_shmImage);
                _shmImage = IntPtr.Zero;
            }
            if (_shmInfo.shmaddr != IntPtr.Zero && _shmInfo.shmaddr != (IntPtr)(-1))
            {
                shmdt(_shmInfo.shmaddr);
            }
            if (_shmInfo.shmid >= 0)
            {
                shmctl(_shmInfo.shmid, IPC_RMID, IntPtr.Zero);
            }
            _shmInfo = default;
            _shmWidth = 0;
            _shmHeight = 0;
        }

        private bool EnsureShmSegment(IntPtr display, int w, int h)
        {
            if (_shmImage != IntPtr.Zero && _shmWidth == w && _shmHeight == h)
                return true;

            if (_shmImage != IntPtr.Zero)
                DestroyShmSegment(display);

            int screen = XDefaultScreen(display);
            IntPtr visual = XDefaultVisual(display, screen);
            uint depth = XDefaultDepth(display, screen);

            _shmInfo = new XShmSegmentInfo();
            _shmImage = XShmCreateImage(display, visual, depth, ZPixmap, IntPtr.Zero,
                ref _shmInfo, (uint)w, (uint)h);
            if (_shmImage == IntPtr.Zero)
                return false;

            var img = Marshal.PtrToStructure<XImage>(_shmImage);
            int size = img.bytes_per_line * img.height;

            _shmInfo.shmid = shmget(IPC_PRIVATE, (IntPtr)size, IPC_CREAT | 0x180);
            if (_shmInfo.shmid < 0)
            {
                XDestroyImage(_shmImage);
                _shmImage = IntPtr.Zero;
                return false;
            }

            _shmInfo.shmaddr = shmat(_shmInfo.shmid, IntPtr.Zero, 0);
            if (_shmInfo.shmaddr == (IntPtr)(-1))
            {
                shmctl(_shmInfo.shmid, IPC_RMID, IntPtr.Zero);
                XDestroyImage(_shmImage);
                _shmImage = IntPtr.Zero;
                _shmInfo = default;
                return false;
            }

            // Point XImage.data to the shared memory
            Marshal.WriteIntPtr(_shmImage, Marshal.OffsetOf<XImage>(nameof(XImage.data)).ToInt32(), _shmInfo.shmaddr);
            _shmInfo.readOnly = 0;

            if (XShmAttach(display, ref _shmInfo) == 0)
            {
                shmdt(_shmInfo.shmaddr);
                shmctl(_shmInfo.shmid, IPC_RMID, IntPtr.Zero);
                XDestroyImage(_shmImage);
                _shmImage = IntPtr.Zero;
                _shmInfo = default;
                return false;
            }

            // Mark for removal on last detach, prevents leaks if process crashes
            shmctl(_shmInfo.shmid, IPC_RMID, IntPtr.Zero);

            _shmWidth = w;
            _shmHeight = h;
            return true;
        }

        private SKBitmap CaptureViaShm(IntPtr display, IntPtr targetWindow, int w, int h)
        {
            if (!EnsureShmSegment(display, w, h))
            {
                _shmAvailable = false;
                _logger.AddLog("X11: XShm setup failed, falling back to XGetImage");
                return null;
            }

            if (XShmGetImage(display, targetWindow, _shmImage, 0, 0, AllPlanes) == 0)
            {
                _logger.AddLog("X11: XShmGetImage failed, disabling XShm");
                DestroyShmSegment(display);
                _shmAvailable = false;
                return null;
            }

            var img = Marshal.PtrToStructure<XImage>(_shmImage);
            if (img.bits_per_pixel != 32)
            {
                _logger.AddLog($"X11: XShm unsupported bpp={img.bits_per_pixel}, falling back");
                _shmAvailable = false;
                return null;
            }

            var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            int rowBytes = w * 4;
            unsafe
            {
                var srcSpan = new ReadOnlySpan<byte>(_shmInfo.shmaddr.ToPointer(), img.bytes_per_line * h);
                var dstSpan = new Span<byte>(bitmap.GetPixels().ToPointer(), rowBytes * h);

                if (img.bytes_per_line == rowBytes)
                {
                    srcSpan.CopyTo(dstSpan);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        srcSpan.Slice(y * img.bytes_per_line, rowBytes)
                            .CopyTo(dstSpan.Slice(y * rowBytes, rowBytes));
                    }
                }
            }

            return bitmap;
        }

        private SKBitmap CaptureViaXGetImage(IntPtr display, IntPtr targetWindow, int w, int h)
        {
            IntPtr ximage = IntPtr.Zero;
            try
            {
                ximage = XGetImage(display, targetWindow, 0, 0, (uint)w, (uint)h, AllPlanes, ZPixmap);
                if (ximage == IntPtr.Zero)
                {
                    _logger.AddLog("X11: XGetImage failed (window may not be visible)");
                    return null;
                }

                var img = Marshal.PtrToStructure<XImage>(ximage);

                if (img.bits_per_pixel != 32 && img.bits_per_pixel != 24)
                {
                    _logger.AddLog($"X11: Unsupported bits_per_pixel={img.bits_per_pixel}");
                    return null;
                }

                var bitmap = new SKBitmap(img.width, img.height, SKColorType.Bgra8888, SKAlphaType.Opaque);

                if (img.bits_per_pixel == 32)
                {
                    int dataSize = img.bytes_per_line * img.height;
                    unsafe
                    {
                        var srcSpan = new ReadOnlySpan<byte>(img.data.ToPointer(), dataSize);
                        var dstSpan = new Span<byte>(bitmap.GetPixels().ToPointer(), dataSize);

                        if (img.bytes_per_line == img.width * 4)
                        {
                            srcSpan.CopyTo(dstSpan);
                        }
                        else
                        {
                            int rowBytes = img.width * 4;
                            for (int y = 0; y < img.height; y++)
                            {
                                srcSpan.Slice(y * img.bytes_per_line, rowBytes)
                                    .CopyTo(dstSpan.Slice(y * rowBytes, rowBytes));
                            }
                        }
                    }
                }
                else if (img.bits_per_pixel == 24)
                {
                    int dstRowBytes = img.width * 4;
                    unsafe
                    {
                        byte* src = (byte*)img.data.ToPointer();
                        byte* dst = (byte*)bitmap.GetPixels().ToPointer();
                        for (int y = 0; y < img.height; y++)
                        {
                            byte* srcRow = src + y * img.bytes_per_line;
                            byte* dstRow = dst + y * dstRowBytes;
                            for (int x = 0; x < img.width; x++)
                            {
                                dstRow[x * 4 + 0] = srcRow[x * 3 + 0]; // B
                                dstRow[x * 4 + 1] = srcRow[x * 3 + 1]; // G
                                dstRow[x * 4 + 2] = srcRow[x * 3 + 2]; // R
                                dstRow[x * 4 + 3] = 0xFF;              // A
                            }
                        }
                    }
                }

                return bitmap;
            }
            finally
            {
                if (ximage != IntPtr.Zero)
                {
                    try { XDestroyImage(ximage); } catch { }
                }
            }
        }

        private SKBitmap CaptureViaX11()
        {
            lock (_captureLock)
            {
                IntPtr prevHandler = IntPtr.Zero;
                try
                {
                    IntPtr display = SharedDisplay;
                    if (display == IntPtr.Zero)
                    {
                        _logger.AddLog("X11: Cannot open display (DISPLAY not set?)");
                        return null;
                    }

                    prevHandler = X11Interop.InstallCaptureErrorHandler();

                    long gameXid = _processFinder.WindowId;
                    IntPtr targetWindow;

                    if (gameXid > 0)
                    {
                        targetWindow = new IntPtr(gameXid);
                    }
                    else
                    {
                        IntPtr rootWindow = XDefaultRootWindow(display);
                        if (rootWindow == IntPtr.Zero)
                        {
                            _logger.AddLog("X11: Cannot get root window");
                            return null;
                        }

                        targetWindow = X11Interop.FindWindowByName(display, rootWindow, "Warframe");
                        if (targetWindow == IntPtr.Zero)
                        {
                            _logger.AddLog("X11: Warframe window not found in X11 tree");
                            return null;
                        }
                    }

                    int w, h;
                    bool attrsFailed = false;

                    if (XGetWindowAttributes(display, targetWindow, out var attrs) == 0
                        || attrs.map_state != IsViewable
                        || attrs.width <= 0 || attrs.height <= 0)
                    {
                        var sb = _windowInfo.ScreenBounds;
                        if (sb.Width <= 0 || sb.Height <= 0)
                        {
                            _logger.AddLog("X11: Cannot get window attributes and no screen bounds available");
                            return null;
                        }
                        if (!_rootFallbackLogged)
                        {
                            _logger.AddLog($"X11: Window attributes unavailable, using screen bounds ({sb.Width}x{sb.Height})");
                            _rootFallbackLogged = true;
                        }
                        w = sb.Width;
                        h = sb.Height;
                        attrsFailed = true;
                    }
                    else
                    {
                        w = attrs.width;
                        h = attrs.height;
                    }

                    XSync(display, false);

                    ProbeShm(display);

                    SKBitmap result = null;

                    if (!attrsFailed && _shmAvailable)
                    {
                        result = CaptureViaShm(display, targetWindow, w, h);
                    }

                    if (result == null)
                    {
                        XSync(display, false);
                        X11Interop.LastXError = 0;
                        result = CaptureViaXGetImage(display, targetWindow, w, h);
                    }

                    XSync(display, false);

                    if (X11Interop.LastXError != 0)
                    {
                        _logger.AddLog($"X11: Protocol error {X11Interop.LastXError} during capture, discarding");
                        result?.Dispose();
                        return null;
                    }

                    return result;
                }
                catch (DllNotFoundException)
                {
                    _logger.AddLog("X11: libX11.so.6 not found");
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.AddLog($"X11 capture error: {ex.Message}");
                    return null;
                }
                finally
                {
                    if (prevHandler != IntPtr.Zero)
                        X11Interop.RestoreErrorHandler(prevHandler);
                }
            }
        }
    }
}
using System;
using System.Runtime.InteropServices;

namespace WFInfo.Linux.Services;

/// <summary>
/// Shared X11 P/Invoke declarations used across Linux services.
/// </summary>
internal static class X11Interop
{
    [DllImport("libX11.so.6")]
    internal static extern IntPtr XOpenDisplay(string display_name);

    [DllImport("libX11.so.6")]
    internal static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    internal static extern int XInitThreads();

    [DllImport("libX11.so.6")]
    internal static extern int XSync(IntPtr display, bool discard);

    // XShm
    [DllImport("libXext.so.6")]
    internal static extern int XShmQueryExtension(IntPtr display);

    [DllImport("libXext.so.6")]
    internal static extern IntPtr XShmCreateImage(IntPtr display, IntPtr visual,
        uint depth, int format, IntPtr data, ref XShmSegmentInfo shminfo,
        uint width, uint height);

    [DllImport("libXext.so.6")]
    internal static extern int XShmAttach(IntPtr display, ref XShmSegmentInfo shminfo);

    [DllImport("libXext.so.6")]
    internal static extern int XShmDetach(IntPtr display, ref XShmSegmentInfo shminfo);

    [DllImport("libXext.so.6")]
    internal static extern int XShmGetImage(IntPtr display, IntPtr drawable,
        IntPtr image, int x, int y, ulong plane_mask);

    [DllImport("libX11.so.6")]
    internal static extern IntPtr XDefaultVisual(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    internal static extern uint XDefaultDepth(IntPtr display, int screen);

    // POSIX SHM
    [DllImport("libc.so.6", EntryPoint = "shmget")]
    internal static extern int shmget(int key, IntPtr size, int shmflg);

    [DllImport("libc.so.6", EntryPoint = "shmat")]
    internal static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);

    [DllImport("libc.so.6", EntryPoint = "shmdt")]
    internal static extern int shmdt(IntPtr shmaddr);

    [DllImport("libc.so.6", EntryPoint = "shmctl")]
    internal static extern int shmctl(int shmid, int cmd, IntPtr buf);

    internal const int IPC_PRIVATE = 0;
    internal const int IPC_CREAT = 0x0200;
    internal const int IPC_RMID = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XShmSegmentInfo
    {
        public IntPtr shmseg;   // ShmSeg (unsigned long)
        public int shmid;
        public IntPtr shmaddr;
        public int readOnly;    // Bool
    }

    private static readonly object _displayLock = new object();
    private static IntPtr _sharedDisplay;
    private static bool _threadsInitialized;

    internal static IntPtr SharedDisplay
    {
        get
        {
            if (_sharedDisplay != IntPtr.Zero)
                return _sharedDisplay;

            lock (_displayLock)
            {
                if (_sharedDisplay != IntPtr.Zero)
                    return _sharedDisplay;

                if (!_threadsInitialized)
                {
                    XInitThreads();
                    _threadsInitialized = true;
                }

                _sharedDisplay = XOpenDisplay(null);
                return _sharedDisplay;
            }
        }
    }

    [DllImport("libX11.so.6")]
    internal static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    internal static extern int XGetWindowAttributes(IntPtr display, IntPtr window,
        out XWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    internal static extern int XQueryTree(IntPtr display, IntPtr window,
        out IntPtr root_return, out IntPtr parent_return,
        out IntPtr children_return, out uint nchildren_return);

    [DllImport("libX11.so.6")]
    internal static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr name_return);

    [DllImport("libX11.so.6")]
    internal static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    internal static extern int XTranslateCoordinates(IntPtr display, IntPtr src_w, IntPtr dest_w,
        int src_x, int src_y, out int dest_x_return, out int dest_y_return, out IntPtr child_return);

    [DllImport("libX11.so.6")]
    internal static extern int XDisplayWidth(IntPtr display, int screen_number);

    [DllImport("libX11.so.6")]
    internal static extern int XDisplayHeight(IntPtr display, int screen_number);

    [DllImport("libX11.so.6")]
    internal static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    internal static extern IntPtr XGetImage(IntPtr display, IntPtr drawable,
        int x, int y, uint width, uint height, ulong plane_mask, int format);

    [DllImport("libX11.so.6")]
    internal static extern int XDestroyImage(IntPtr ximage);

    [StructLayout(LayoutKind.Sequential)]
    internal struct XWindowAttributes
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public int depth;
        private IntPtr visual;
        private IntPtr root;
        private int _class;
        private int bit_gravity, win_gravity;
        private int backing_store;
        private ulong backing_planes, backing_pixel;
        private int save_under;
        private IntPtr colormap;
        private int map_installed;
        internal int map_state;
        private long all_event_masks, your_event_masks, do_not_propagate_mask;
        private int override_redirect;
        private IntPtr screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XImage
    {
        public int width, height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
    }

    [DllImport("libX11.so.6")]
    internal static extern IntPtr XResourceManagerString(IntPtr display);

    [DllImport("libX11.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool XQueryPointer(IntPtr display, IntPtr window,
        out IntPtr root_return, out IntPtr child_return,
        out int root_x_return, out int root_y_return,
        out int win_x_return, out int win_y_return,
        out uint mask_return);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int XErrorHandlerDelegate(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")]
    internal static extern IntPtr XSetErrorHandler(XErrorHandlerDelegate handler);

    [DllImport("libX11.so.6", EntryPoint = "XSetErrorHandler")]
    internal static extern IntPtr XSetErrorHandlerRaw(IntPtr handler);

    [DllImport("libX11.so.6")]
    internal static extern void XLockDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    internal static extern void XUnlockDisplay(IntPtr display);

    internal static volatile int LastXError;

    private static readonly XErrorHandlerDelegate _captureErrorHandler = CaptureErrorHandler;
    private static int CaptureErrorHandler(IntPtr display, IntPtr errorEvent)
    {
        // XErrorEvent on x86_64: error_code is at offset 32
        byte errorCode = Marshal.ReadByte(errorEvent, 32);
        LastXError = errorCode;
        return 0;
    }

    internal static IntPtr InstallCaptureErrorHandler()
    {
        LastXError = 0;
        return XSetErrorHandler(_captureErrorHandler);
    }

    internal static void RestoreErrorHandler(IntPtr previous)
    {
        XSetErrorHandlerRaw(previous);
    }

    internal static bool IsXWayland { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    internal const int IsViewable = 2;
    internal const int ZPixmap = 2;
    internal const ulong AllPlanes = 0xFFFFFFFFFFFFFFFF;

    // XRandR - per-monitor geometry
    [DllImport("libXrandr.so.2")]
    internal static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window,
        int get_active, out int nmonitors);

    [DllImport("libXrandr.so.2")]
    internal static extern void XRRFreeMonitors(IntPtr monitors);

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    internal struct XRRMonitorInfo
    {
        [FieldOffset(0)]  public ulong name;
        [FieldOffset(8)]  public int primary;
        [FieldOffset(12)] public int automatic;
        [FieldOffset(16)] public int noutput;
        [FieldOffset(20)] public int x;
        [FieldOffset(24)] public int y;
        [FieldOffset(28)] public int width;
        [FieldOffset(32)] public int height;
        [FieldOffset(36)] public int mwidth;
        [FieldOffset(40)] public int mheight;
        [FieldOffset(48)] public IntPtr outputs;
    }

    internal static (int x, int y, int w, int h) GetMonitorAtPoint(IntPtr display, int px, int py)
    {
        int screen = XDefaultScreen(display);
        int rx = 0, ry = 0;
        int rw = XDisplayWidth(display, screen);
        int rh = XDisplayHeight(display, screen);

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            IntPtr monPtr = XRRGetMonitors(display, root, 1, out int nMonitors);

            if (monPtr != IntPtr.Zero && nMonitors > 0)
            {
                int structSize = Marshal.SizeOf<XRRMonitorInfo>();
                for (int i = 0; i < nMonitors; i++)
                {
                    var mi = Marshal.PtrToStructure<XRRMonitorInfo>(monPtr + i * structSize);
                    if (px >= mi.x && px < mi.x + mi.width &&
                        py >= mi.y && py < mi.y + mi.height)
                    {
                        rx = mi.x;
                        ry = mi.y;
                        rw = mi.width;
                        rh = mi.height;
                        break;
                    }
                }
                XRRFreeMonitors(monPtr);
            }
        }
        catch (DllNotFoundException) { }

        return (rx, ry, rw, rh);
    }

    /// <summary>
    /// Recursively searches the X11 window tree for a window whose name
    /// contains the search string (case-insensitive). Skips tiny windows (&lt;100x100).
    /// </summary>
    internal static IntPtr FindWindowByName(IntPtr display, IntPtr parent, string search, int depth = 0)
    {
        if (depth > 5) return IntPtr.Zero;

        if (XQueryTree(display, parent, out _, out _, out IntPtr children, out uint nchildren) == 0)
            return IntPtr.Zero;

        if (children == IntPtr.Zero || nchildren == 0)
            return IntPtr.Zero;

        IntPtr result = IntPtr.Zero;

        for (uint i = 0; i < nchildren && result == IntPtr.Zero; i++)
        {
            IntPtr child = Marshal.ReadIntPtr(children, (int)(i * IntPtr.Size));

            if (XFetchName(display, child, out IntPtr namePtr) != 0 && namePtr != IntPtr.Zero)
            {
                string name = Marshal.PtrToStringAnsi(namePtr);
                XFree(namePtr);

                if (name != null && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (XGetWindowAttributes(display, child, out var attrs) != 0
                        && attrs.width > 100 && attrs.height > 100)
                    {
                        result = child;
                    }
                }
            }

            if (result == IntPtr.Zero)
                result = FindWindowByName(display, child, search, depth + 1);
        }

        XFree(children);
        return result;
    }
}
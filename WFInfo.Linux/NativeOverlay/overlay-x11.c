#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <time.h>
#include <errno.h>
#include <linux/input.h>
#include <dirent.h>
#include <fcntl.h>
#include <sys/ioctl.h>
#include <sys/inotify.h>
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <X11/extensions/Xfixes.h>
#include <X11/extensions/Xrender.h>
#include <X11/extensions/shape.h>
#include <X11/extensions/XInput2.h>
#include <cairo/cairo.h>
#include <cairo/cairo-xlib.h>
#include <cairo/cairo-xlib-xrender.h>
#include <X11/Xlib-xcb.h>
#include <xcb/xcb.h>
#include "overlay.h"

static Display *x_dpy;
static int x_screen;
static Window x_root;
static Visual *x_argb_visual;
static int x_argb_depth;
static Colormap x_argb_cmap;
static int x_screen_w, x_screen_h;
static Atom x_bypass_compositor;
static XRenderPictFormat *x_argb_fmt;
static Cursor x_invis_cursor;

typedef struct {
    Window win;
    int created;
    cairo_surface_t *cs;
    unsigned char *buf;
    int buf_w, buf_h;
} X11Panel;

static X11Panel x_panels[MAX_PANELS];

static struct {
    Window win;
    int created;
    cairo_surface_t *cs;
    cairo_surface_t *content_cache;
    Pixmap content_pm;
    Picture content_pic;
    Picture win_pic;
    int content_w, content_h;
    int last_ox, last_oy;
} x_rw;

static struct {
    Window win;
    int created;
    Pixmap bg_pm;
    Pixmap back_pm;
    Picture back_pic;
    GC copy_gc;
    GC dash_gc;
    char dashes[2];
    int prev_rx, prev_ry;
    unsigned int prev_rw, prev_rh;
    int has_prev;
    int local_dash;
    long long last_anim_ns;
} x_snapit;

static xcb_connection_t *xcb_conn;
static xcb_query_pointer_cookie_t qp_cookie;
static int qp_pending;

static int xi_opcode = -1;
static int evdev_fd = -1;
static int inotify_fd = -1;
static int x11_btn_prev;
static int last_ptr_x, last_ptr_y;

#define CURSOR_W 14
#define CURSOR_H 22
static Pixmap cursor_pm;
static Picture cursor_pic;
static int cursor_on, cursor_lx, cursor_ly;

static void create_cursor_server(void)
{
    cursor_pm = XCreatePixmap(x_dpy, x_root, CURSOR_W, CURSOR_H, 32);
    XRenderPictFormat *fmt = XRenderFindStandardFormat(x_dpy, PictStandardARGB32);
    cairo_surface_t *cs = cairo_xlib_surface_create_with_xrender_format(
        x_dpy, cursor_pm, ScreenOfDisplay(x_dpy, x_screen), fmt, CURSOR_W, CURSOR_H);
    cairo_t *cr = cairo_create(cs);
    cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
    cairo_set_source_rgba(cr, 0, 0, 0, 0);
    cairo_paint(cr);
    cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
    cairo_move_to(cr, 0, 0);
    cairo_line_to(cr, 0, 18);
    cairo_line_to(cr, 4, 14);
    cairo_line_to(cr, 8, 21);
    cairo_line_to(cr, 11, 19);
    cairo_line_to(cr, 7, 12);
    cairo_line_to(cr, 13, 12);
    cairo_close_path(cr);
    cairo_set_source_rgba(cr, 1, 1, 1, 1);
    cairo_fill_preserve(cr);
    cairo_set_source_rgba(cr, 0, 0, 0, 1);
    cairo_set_line_width(cr, 1.2);
    cairo_stroke(cr);
    cairo_destroy(cr);
    cairo_surface_flush(cs);
    cairo_surface_destroy(cs);
    cursor_pic = XRenderCreatePicture(x_dpy, cursor_pm, fmt, 0, NULL);
}

static Visual *find_argb_visual(Display *dpy, int screen, int *depth_out)
{
    XVisualInfo tpl;
    tpl.screen = screen;
    tpl.depth = 32;
    tpl.class = TrueColor;
    int count = 0;
    XVisualInfo *vis = XGetVisualInfo(dpy, VisualScreenMask | VisualDepthMask | VisualClassMask, &tpl, &count);
    if (!vis || count == 0) return NULL;

    Visual *result = NULL;
    for (int i = 0; i < count; i++) {
        XRenderPictFormat *fmt = XRenderFindVisualFormat(dpy, vis[i].visual);
        if (fmt && fmt->type == PictTypeDirect && fmt->direct.alphaMask) {
            result = vis[i].visual;
            *depth_out = vis[i].depth;
            break;
        }
    }
    XFree(vis);
    return result;
}

static Window create_overlay_window(int x, int y, int w, int h)
{
    XSetWindowAttributes attrs;
    attrs.override_redirect = True;
    attrs.colormap = x_argb_cmap;
    attrs.background_pixmap = None;
    attrs.border_pixel = 0;

    Window win = XCreateWindow(x_dpy, x_root, x, y, w, h, 0,
        x_argb_depth, InputOutput, x_argb_visual,
        CWOverrideRedirect | CWColormap | CWBackPixmap | CWBorderPixel, &attrs);

    long val = 2;
    XChangeProperty(x_dpy, win, x_bypass_compositor, XA_CARDINAL, 32, PropModeReplace,
        (unsigned char *)&val, 1);

    return win;
}

static void set_click_through(Window win)
{
    XserverRegion region = XFixesCreateRegion(x_dpy, NULL, 0);
    XFixesSetWindowShapeRegion(x_dpy, win, ShapeInput, 0, 0, region);
    XFixesDestroyRegion(x_dpy, region);
}


static void raise_window(Window win)
{
    XRaiseWindow(x_dpy, win);
}

static void open_evdev_mouse(void)
{
    DIR *dir = opendir("/dev/input");
    if (!dir) return;
    struct dirent *ent;
    while ((ent = readdir(dir)) != NULL) {
        if (strncmp(ent->d_name, "event", 5) != 0) continue;
        char path[280];
        snprintf(path, sizeof(path), "/dev/input/%s", ent->d_name);
        int fd = open(path, O_RDONLY | O_NONBLOCK);
        if (fd < 0) continue;
        unsigned long evbits[(EV_MAX + 1 + 31) / 32];
        memset(evbits, 0, sizeof(evbits));
        if (ioctl(fd, EVIOCGBIT(0, sizeof(evbits)), evbits) < 0) {
            close(fd);
            continue;
        }
        int has_rel = (evbits[EV_REL / 32] >> (EV_REL % 32)) & 1;
        unsigned long keybits[(KEY_MAX + 1 + 31) / 32];
        memset(keybits, 0, sizeof(keybits));
        ioctl(fd, EVIOCGBIT(EV_KEY, sizeof(keybits)), keybits);
        int has_btn = (keybits[BTN_LEFT / 32] >> (BTN_LEFT % 32)) & 1;
        if (has_rel && has_btn) {
            evdev_fd = fd;
            fprintf(stderr, "wfinfo-overlay: evdev mouse: %s\n", path);
            break;
        }
        close(fd);
    }
    closedir(dir);
    if (evdev_fd < 0)
        fprintf(stderr, "wfinfo-overlay: no evdev mouse found"
                " (falling back to XI2 only)\n");
}

static void start_inotify_watch(void)
{
    inotify_fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
    if (inotify_fd < 0) return;
    if (inotify_add_watch(inotify_fd, "/dev/input", IN_CREATE) < 0) {
        close(inotify_fd);
        inotify_fd = -1;
    }
}

static void check_evdev_hotplug(void)
{
    if (inotify_fd < 0) return;
    char buf[4096] __attribute__((aligned(__alignof__(struct inotify_event))));
    int got_event = 0;
    for (;;) {
        ssize_t len = read(inotify_fd, buf, sizeof(buf));
        if (len <= 0) break;
        const struct inotify_event *ev;
        for (char *p = buf; p < buf + len;
             p += sizeof(struct inotify_event) + ev->len) {
            ev = (const struct inotify_event *)p;
            if (ev->len > 0 && strncmp(ev->name, "event", 5) == 0)
                got_event = 1;
        }
    }
    if (got_event && evdev_fd < 0) {
        usleep(300000);
        open_evdev_mouse();
    }
}

static void x11_rw_blit(void);
static void x11_rw_redraw(void);
static void x11_snapit_redraw(void);
static void x11_close_snapit(void);
static void panel_paint(X11Panel *xp);

static int x11_init(void)
{
    x_dpy = XOpenDisplay(NULL);
    if (!x_dpy) return -1;

    x_screen = DefaultScreen(x_dpy);
    x_root = RootWindow(x_dpy, x_screen);
    x_screen_w = DisplayWidth(x_dpy, x_screen);
    x_screen_h = DisplayHeight(x_dpy, x_screen);

    x_argb_visual = find_argb_visual(x_dpy, x_screen, &x_argb_depth);
    if (!x_argb_visual) {
        fprintf(stderr, "wfinfo-overlay: no ARGB visual found\n");
        XCloseDisplay(x_dpy);
        x_dpy = NULL;
        return -1;
    }

    x_argb_cmap = XCreateColormap(x_dpy, x_root, x_argb_visual, AllocNone);
    x_bypass_compositor = XInternAtom(x_dpy, "_NET_WM_BYPASS_COMPOSITOR", False);
    x_argb_fmt = XRenderFindVisualFormat(x_dpy, x_argb_visual);
    {
        Pixmap blank = XCreatePixmap(x_dpy, x_root, 1, 1, 1);
        XColor dummy = {0};
        x_invis_cursor = XCreatePixmapCursor(x_dpy, blank, blank, &dummy, &dummy, 0, 0);
        XFreePixmap(x_dpy, blank);
    }

    {
        int xi_event, xi_error;
        if (XQueryExtension(x_dpy, "XInputExtension",
                            &xi_opcode, &xi_event, &xi_error)) {
            int major = 2, minor = 0;
            if (XIQueryVersion(x_dpy, &major, &minor) == Success) {
                unsigned char mask_bytes[(XI_LASTEVENT + 7) / 8];
                memset(mask_bytes, 0, sizeof(mask_bytes));
                XISetMask(mask_bytes, XI_RawButtonPress);
                XISetMask(mask_bytes, XI_RawButtonRelease);
                XIEventMask evmask;
                evmask.deviceid = XIAllMasterDevices;
                evmask.mask_len = sizeof(mask_bytes);
                evmask.mask = mask_bytes;
                XISelectEvents(x_dpy, x_root, &evmask, 1);
            } else {
                xi_opcode = -1;
            }
        }
    }

    open_evdev_mouse();
    start_inotify_watch();

    if (x_argb_fmt) {
        XRenderPictFormat *fmt = x_argb_fmt;
        fprintf(stderr, "wfinfo-overlay: ARGB visual depth=%d "
                "r=%d/%d g=%d/%d b=%d/%d a=%d/%d\n",
                x_argb_depth,
                fmt->direct.red, fmt->direct.redMask,
                fmt->direct.green, fmt->direct.greenMask,
                fmt->direct.blue, fmt->direct.blueMask,
                fmt->direct.alpha, fmt->direct.alphaMask);
    }

    xcb_conn = XGetXCBConnection(x_dpy);
    XSelectInput(x_dpy, x_root, SubstructureNotifyMask);

    memset(x_panels, 0, sizeof(x_panels));
    memset(&x_rw, 0, sizeof(x_rw));
    memset(&x_snapit, 0, sizeof(x_snapit));

    return 0;
}

static void x11_destroy(void)
{
    for (int i = 0; i < MAX_PANELS; i++) {
        if (x_panels[i].cs) cairo_surface_destroy(x_panels[i].cs);
        if (x_panels[i].created) XDestroyWindow(x_dpy, x_panels[i].win);
        if (x_panels[i].buf) free(x_panels[i].buf);
    }
    if (cursor_pic) { XRenderFreePicture(x_dpy, cursor_pic); cursor_pic = 0; }
    if (cursor_pm) { XFreePixmap(x_dpy, cursor_pm); cursor_pm = 0; }
    if (x_invis_cursor) { XFreeCursor(x_dpy, x_invis_cursor); x_invis_cursor = 0; }
    if (x_rw.win_pic) XRenderFreePicture(x_dpy, x_rw.win_pic);
    if (x_rw.content_pic) XRenderFreePicture(x_dpy, x_rw.content_pic);
    if (x_rw.content_cache) cairo_surface_destroy(x_rw.content_cache);
    if (x_rw.content_pm) XFreePixmap(x_dpy, x_rw.content_pm);
    if (x_rw.cs) cairo_surface_destroy(x_rw.cs);
    if (x_rw.created) XDestroyWindow(x_dpy, x_rw.win);
    x11_close_snapit();
    if (evdev_fd >= 0) { close(evdev_fd); evdev_fd = -1; }
    if (inotify_fd >= 0) { close(inotify_fd); inotify_fd = -1; }
    if (qp_pending) {
        free(xcb_query_pointer_reply(xcb_conn, qp_cookie, NULL));
        qp_pending = 0;
    }
    if (x_argb_cmap) XFreeColormap(x_dpy, x_argb_cmap);
    if (x_dpy) XCloseDisplay(x_dpy);
}

static int x11_get_fd(void) { return ConnectionNumber(x_dpy); }

static int x11_dispatch(void)
{
    while (XPending(x_dpy)) {
        XEvent ev;
        XNextEvent(x_dpy, &ev);

        if (ev.type == GenericEvent && xi_opcode >= 0 &&
            ev.xcookie.extension == xi_opcode) {
            if (XGetEventData(x_dpy, &ev.xcookie)) {
                XIRawEvent *raw = (XIRawEvent *)ev.xcookie.data;
                if (x_rw.created && rw.visible && rw.configured) {
                    switch (raw->evtype) {
                    case XI_RawButtonPress:
                        if (raw->detail == 1 && !rw.dragging) {
                            int over = (last_ptr_x >= rw.offset_x &&
                                        last_ptr_x <  rw.offset_x + rw.total_w &&
                                        last_ptr_y >= rw.offset_y &&
                                        last_ptr_y <  rw.offset_y + RW_TOTAL_H);
                            if (over) {
                                pointer_on_rw = 1;
                                handle_rw_button_press(last_ptr_x, last_ptr_y);
                            }
                        }
                        break;
                    case XI_RawButtonRelease:
                        if (raw->detail == 1 && rw.dragging)
                            handle_rw_button_release();
                        break;
                    }
                }
                XFreeEventData(x_dpy, &ev.xcookie);
            }
            continue;
        }

        if (ev.type == ButtonPress && ev.xbutton.button == Button1) {
            if (x_snapit.created && ev.xbutton.window == x_snapit.win)
                handle_snapit_press(ev.xbutton.x, ev.xbutton.y);
        } else if (ev.type == ButtonRelease && ev.xbutton.button == Button1) {
            if (x_snapit.created && ev.xbutton.window == x_snapit.win)
                handle_snapit_release(ev.xbutton.x, ev.xbutton.y);
        } else if (ev.type == MotionNotify) {
            if (x_snapit.created && ev.xmotion.window == x_snapit.win) {
                while (XCheckTypedWindowEvent(x_dpy, x_snapit.win,
                                              MotionNotify, &ev))
                    ;
                handle_snapit_motion(ev.xmotion.x, ev.xmotion.y);
                x11_snapit_redraw();
            }
        } else if (ev.type == KeyPress) {
            if (x_snapit.created && snapit.active) {
                printf("{\"event\":\"snapit_cancel\"}\n");
                fflush(stdout);
                x11_close_snapit();
                snapit.active = 0;
                snapit.configured = 0;
                snapit.dragging = 0;
            }
        } else if (ev.type == Expose && ev.xexpose.count == 0) {
            if (x_rw.created && ev.xexpose.window == x_rw.win)
                x11_rw_blit();
            else if (x_snapit.created && ev.xexpose.window == x_snapit.win
                     && x_snapit.back_pm)
                XCopyArea(x_dpy, x_snapit.back_pm, x_snapit.win,
                    x_snapit.copy_gc,
                    ev.xexpose.x, ev.xexpose.y,
                    ev.xexpose.width, ev.xexpose.height,
                    ev.xexpose.x, ev.xexpose.y);
            else {
                for (int i = 0; i < MAX_PANELS; i++) {
                    if (x_panels[i].created && ev.xexpose.window == x_panels[i].win) {
                        panel_paint(&x_panels[i]);
                        break;
                    }
                }
            }
        } else if (ev.type == ConfigureNotify || ev.type == MapNotify) {
            if (x_rw.created && rw.visible) {
                Window changed = (ev.type == ConfigureNotify) ?
                    ev.xconfigure.window : ev.xmap.window;
                if (changed != x_rw.win)
                    raise_window(x_rw.win);
            }
        }
    }

    return 0;
}

static void x11_flush(void)
{
    XFlush(x_dpy);
}

static void panel_paint(X11Panel *xp)
{
    if (!xp->created || !xp->cs || !xp->buf) return;

    cairo_surface_t *img = cairo_image_surface_create_for_data(
        xp->buf, CAIRO_FORMAT_ARGB32, xp->buf_w, xp->buf_h, xp->buf_w * 4);
    cairo_t *cr = cairo_create(xp->cs);
    cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
    cairo_set_source_surface(cr, img, 0, 0);
    cairo_paint(cr);
    cairo_destroy(cr);
    cairo_surface_destroy(img);

    cairo_surface_flush(xp->cs);
    XFlush(x_dpy);
}

static void x11_show_panel(int id)
{
    Panel *p = &panels[id];
    X11Panel *xp = &x_panels[id];

    if (xp->cs) { cairo_surface_destroy(xp->cs); xp->cs = NULL; }
    if (xp->created) { XDestroyWindow(x_dpy, xp->win); xp->created = 0; }
    if (xp->buf) { free(xp->buf); xp->buf = NULL; }

    xp->buf_w = p->w;
    xp->buf_h = p->h;
    size_t sz = (size_t)p->w * p->h * 4;
    xp->buf = malloc(sz);
    if (!xp->buf) return;
    memset(xp->buf, 0, sz);
    render_panel(p, xp->buf, 1);

    xp->win = create_overlay_window(p->x, p->y, p->w, p->h);
    xp->created = 1;

    XSelectInput(x_dpy, xp->win, ExposureMask);
    set_click_through(xp->win);

    xp->cs = cairo_xlib_surface_create_with_xrender_format(
        x_dpy, xp->win, ScreenOfDisplay(x_dpy, x_screen),
        x_argb_fmt, p->w, p->h);

    XMapRaised(x_dpy, xp->win);
    raise_window(xp->win);

    panel_paint(xp);
    p->configured = 1;
}

static void x11_hide_panel(int id)
{
    X11Panel *xp = &x_panels[id];
    if (xp->cs) { cairo_surface_destroy(xp->cs); xp->cs = NULL; }
    if (xp->created) { XDestroyWindow(x_dpy, xp->win); xp->created = 0; }
    if (xp->buf) { free(xp->buf); xp->buf = NULL; }
}

static void x11_rerender_panel(int id)
{
    Panel *p = &panels[id];
    X11Panel *xp = &x_panels[id];
    if (!xp->created || !xp->cs || !xp->buf) return;

    memset(xp->buf, 0, xp->buf_w * xp->buf_h * 4);
    render_panel(p, xp->buf, 1);
    panel_paint(xp);
}

static void x11_show_rw(void)
{
    if (x_rw.win_pic) { XRenderFreePicture(x_dpy, x_rw.win_pic); x_rw.win_pic = 0; }
    if (x_rw.content_pic) { XRenderFreePicture(x_dpy, x_rw.content_pic); x_rw.content_pic = 0; }
    if (x_rw.content_cache) { cairo_surface_destroy(x_rw.content_cache); x_rw.content_cache = NULL; }
    if (x_rw.content_pm) { XFreePixmap(x_dpy, x_rw.content_pm); x_rw.content_pm = 0; }
    x_rw.content_w = 0; x_rw.content_h = 0;
    if (x_rw.cs) { cairo_surface_destroy(x_rw.cs); x_rw.cs = NULL; }
    if (x_rw.created) { XDestroyWindow(x_dpy, x_rw.win); x_rw.created = 0; }

    if (!rw.dragging && !rw.visible) {
        Window wr, cr; int wx, wy; unsigned int wm;
        int px = x_screen_w / 2, py = x_screen_h / 2;
        if (XQueryPointer(x_dpy, x_root, &wr, &cr, &px, &py, &wx, &wy, &wm)) {}
        MonitorRect mon;
        get_monitor_at_point(x_dpy, px, py, &mon);
        rw.offset_x = mon.x + (mon.w - rw.total_w) / 2;
        rw.offset_y = mon.y + (mon.h - RW_TOTAL_H) / 2;
    }

    rw.surf_w = rw.total_w;
    rw.surf_h = RW_TOTAL_H;

    x_rw.win = create_overlay_window(rw.offset_x, rw.offset_y, rw.surf_w, rw.surf_h);
    x_rw.created = 1;

    XSelectInput(x_dpy, x_rw.win, ExposureMask);

    XDefineCursor(x_dpy, x_rw.win, x_invis_cursor);

    x_rw.cs = cairo_xlib_surface_create_with_xrender_format(
        x_dpy, x_rw.win, ScreenOfDisplay(x_dpy, x_screen),
        x_argb_fmt, rw.surf_w, rw.surf_h);

    x_rw.win_pic = XRenderCreatePicture(x_dpy, x_rw.win,
        x_argb_fmt, 0, NULL);

    rw.configured = 1;
    x11_rw_redraw();

    XMapRaised(x_dpy, x_rw.win);
    raise_window(x_rw.win);
}

static void x11_hide_rw(void)
{
    cursor_on = 0;
    x11_btn_prev = 0;
    if (qp_pending) {
        free(xcb_query_pointer_reply(xcb_conn, qp_cookie, NULL));
        qp_pending = 0;
    }
    if (x_rw.win_pic) { XRenderFreePicture(x_dpy, x_rw.win_pic); x_rw.win_pic = 0; }
    if (x_rw.content_pic) { XRenderFreePicture(x_dpy, x_rw.content_pic); x_rw.content_pic = 0; }
    if (x_rw.content_cache) { cairo_surface_destroy(x_rw.content_cache); x_rw.content_cache = NULL; }
    if (x_rw.content_pm) { XFreePixmap(x_dpy, x_rw.content_pm); x_rw.content_pm = 0; }
    x_rw.content_w = 0; x_rw.content_h = 0;
    if (x_rw.cs) { cairo_surface_destroy(x_rw.cs); x_rw.cs = NULL; }
    if (x_rw.created) { XDestroyWindow(x_dpy, x_rw.win); x_rw.created = 0; }
}

static void x11_rw_blit(void)
{
    if (!x_rw.win_pic || !x_rw.content_pic) return;
    XRenderComposite(x_dpy, PictOpSrc,
        x_rw.content_pic, None, x_rw.win_pic,
        0, 0, 0, 0, 0, 0, rw.surf_w, rw.surf_h);
    if (cursor_on && cursor_pic)
        XRenderComposite(x_dpy, PictOpOver,
            cursor_pic, None, x_rw.win_pic,
            0, 0, 0, 0, cursor_lx, cursor_ly, CURSOR_W, CURSOR_H);
}

static void x11_rw_redraw(void)
{
    if (!x_rw.created || !x_rw.cs || !rw.configured) return;

    if (rw.offset_x != x_rw.last_ox || rw.offset_y != x_rw.last_oy) {
        XMoveWindow(x_dpy, x_rw.win, rw.offset_x, rw.offset_y);
        x_rw.last_ox = rw.offset_x;
        x_rw.last_oy = rw.offset_y;
    }

    int w = rw.total_w;
    int h = RW_TOTAL_H;

    if (x_rw.content_w != w || x_rw.content_h != h) {
        if (x_rw.content_pic) { XRenderFreePicture(x_dpy, x_rw.content_pic); x_rw.content_pic = 0; }
        if (x_rw.content_cache) { cairo_surface_destroy(x_rw.content_cache); x_rw.content_cache = NULL; }
        if (x_rw.content_pm) { XFreePixmap(x_dpy, x_rw.content_pm); x_rw.content_pm = 0; }
        x_rw.content_pm = XCreatePixmap(x_dpy, x_rw.win, w, h, x_argb_depth);
        x_rw.content_cache = cairo_xlib_surface_create_with_xrender_format(
            x_dpy, x_rw.content_pm, ScreenOfDisplay(x_dpy, x_screen), x_argb_fmt, w, h);
        x_rw.content_pic = XRenderCreatePicture(x_dpy, x_rw.content_pm, x_argb_fmt, 0, NULL);
        x_rw.content_w = w;
        x_rw.content_h = h;
    }

    cairo_t *cr = cairo_create(x_rw.content_cache);
    cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
    cairo_set_source_rgba(cr, 0.106, 0.106, 0.106, 1.0);
    cairo_paint(cr);
    cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
    render_rw_content(cr);
    cairo_destroy(cr);
    cairo_surface_flush(x_rw.content_cache);

    x11_rw_blit();
    XFlush(x_dpy);
}

static void x11_rw_set_input_region(int fullscreen)
{
    (void)fullscreen;
}

static void drain_events(void)
{
    XSync(x_dpy, False);
    while (XPending(x_dpy)) {
        XEvent ev;
        XNextEvent(x_dpy, &ev);
    }
}

static void break_wine_grab(int cx, int cy)
{
    Window tmp = XCreateSimpleWindow(x_dpy, x_root,
        cx, cy, 1, 1, 0, 0, 0);
    XMapWindow(x_dpy, tmp);
    Atom net_active = XInternAtom(x_dpy, "_NET_ACTIVE_WINDOW", False);
    XEvent msg;
    memset(&msg, 0, sizeof(msg));
    msg.xclient.type         = ClientMessage;
    msg.xclient.window       = tmp;
    msg.xclient.message_type = net_active;
    msg.xclient.format       = 32;
    msg.xclient.data.l[0]    = 2;
    msg.xclient.data.l[1]    = CurrentTime;
    XSendEvent(x_dpy, x_root, False,
        SubstructureNotifyMask | SubstructureRedirectMask, &msg);
    drain_events();
    usleep(20000);
    drain_events();
    XDestroyWindow(x_dpy, tmp);
    drain_events();
}

static void apply_white_tint(Pixmap pm, int w, int h, double alpha)
{
    Visual *vis = DefaultVisual(x_dpy, x_screen);
    XRenderPictFormat *fmt_vis = XRenderFindVisualFormat(x_dpy, vis);
    XRenderPictFormat *fmt_a32 = XRenderFindStandardFormat(x_dpy, PictStandardARGB32);
    Pixmap src_pm = XCreatePixmap(x_dpy, pm, 1, 1, 32);
    XRenderPictureAttributes pa;
    pa.repeat = RepeatNormal;
    Picture src_pic = XRenderCreatePicture(x_dpy, src_pm, fmt_a32, CPRepeat, &pa);
    unsigned short a16 = (unsigned short)(alpha * 65535.0 + 0.5);
    XRenderColor col = { a16, a16, a16, a16 };
    XRenderFillRectangle(x_dpy, PictOpSrc, src_pic, &col, 0, 0, 1, 1);
    Picture dst_pic = XRenderCreatePicture(x_dpy, pm, fmt_vis, 0, NULL);
    XRenderComposite(x_dpy, PictOpOver, src_pic, None, dst_pic,
        0, 0, 0, 0, 0, 0, w, h);
    XRenderFreePicture(x_dpy, dst_pic);
    XRenderFreePicture(x_dpy, src_pic);
    XFreePixmap(x_dpy, src_pm);
}

static void x11_start_snapit(int req_w, int req_h)
{
    (void)req_w; (void)req_h;

    x11_close_snapit();

    Window wr, cr; int px, py, wx, wy; unsigned int pm;
    px = x_screen_w / 2; py = x_screen_h / 2;
    XQueryPointer(x_dpy, x_root, &wr, &cr, &px, &py, &wx, &wy, &pm);

    MonitorRect mon;
    get_monitor_at_point(x_dpy, px, py, &mon);
    int mx = mon.x, my = mon.y, sw = mon.w, sh = mon.h;

    int depth = DefaultDepth(x_dpy, x_screen);
    Visual *vis = DefaultVisual(x_dpy, x_screen);

    snapit.surf_w = sw;
    snapit.surf_h = sh;
    snapit.phys_w = sw;
    snapit.phys_h = sh;
    snapit.origin_x = mx;
    snapit.origin_y = my;
    snapit.dragging = 0;
    snapit.start_x = snapit.start_y = 0;
    snapit.cur_x = snapit.cur_y = 0;
    snapit.dash_offset = 0;

    x_snapit.bg_pm = XCreatePixmap(x_dpy, x_root, sw, sh, depth);
    {
        XGCValues gcv;
        gcv.subwindow_mode = IncludeInferiors;
        GC cap_gc = XCreateGC(x_dpy, x_root, GCSubwindowMode, &gcv);
        XCopyArea(x_dpy, x_root, x_snapit.bg_pm, cap_gc, mx, my, sw, sh, 0, 0);
        XFreeGC(x_dpy, cap_gc);
    }
    XSync(x_dpy, False);

    apply_white_tint(x_snapit.bg_pm, sw, sh, 0.063);
    XSync(x_dpy, False);

    snapit_cache_hint();
    if (snapit.hint_cs) {
        cairo_surface_t *bg_cs = cairo_xlib_surface_create(
            x_dpy, x_snapit.bg_pm, vis, sw, sh);
        cairo_t *cr = cairo_create(bg_cs);
        double tx = (sw - snapit.hint_w) / 2.0;
        double ty = sh - snapit.hint_h - 10;
        cairo_set_source_surface(cr, snapit.hint_cs, tx, ty);
        cairo_paint(cr);
        cairo_destroy(cr);
        cairo_surface_destroy(bg_cs);
        XSync(x_dpy, False);
    }

    x_snapit.back_pm = XCreatePixmap(x_dpy, x_root, sw, sh, depth);
    x_snapit.copy_gc = XCreateGC(x_dpy, x_snapit.back_pm, 0, NULL);
    XCopyArea(x_dpy, x_snapit.bg_pm, x_snapit.back_pm, x_snapit.copy_gc,
        0, 0, sw, sh, 0, 0);

    XRenderPictFormat *fmt_vis = XRenderFindVisualFormat(x_dpy, vis);
    x_snapit.back_pic = XRenderCreatePicture(x_dpy, x_snapit.back_pm,
        fmt_vis, 0, NULL);

    break_wine_grab(mx + sw / 2, my + sh / 2);

    Cursor crosshair = XCreateFontCursor(x_dpy, 34);
    {
        XColor wc = {0}, bc = {0};
        wc.red = wc.green = wc.blue = 65535;
        XRecolorCursor(x_dpy, crosshair, &wc, &bc);
    }

    XSetWindowAttributes wa;
    wa.override_redirect = True;
    wa.background_pixmap = x_snapit.bg_pm;
    wa.cursor            = crosshair;
    wa.event_mask        = ExposureMask | StructureNotifyMask |
                           KeyPressMask | ButtonPressMask |
                           ButtonReleaseMask | PointerMotionMask;

    x_snapit.win = XCreateWindow(x_dpy, x_root, mx, my, sw, sh, 0,
        depth, InputOutput, vis,
        CWOverrideRedirect | CWBackPixmap | CWCursor | CWEventMask, &wa);
    XFreeCursor(x_dpy, crosshair);
    x_snapit.created = 1;

    {
        long val = 2;
        XChangeProperty(x_dpy, x_snapit.win, x_bypass_compositor, XA_CARDINAL, 32,
            PropModeReplace, (unsigned char *)&val, 1);
    }

    XMapRaised(x_dpy, x_snapit.win);
    {
        XEvent ev;
        do { XNextEvent(x_dpy, &ev); }
        while (ev.type != MapNotify || ev.xmap.window != x_snapit.win);
    }

    for (int i = 0; i < 50; i++) {
        int r = XGrabPointer(x_dpy, x_snapit.win, True,
            ButtonPressMask | ButtonReleaseMask | PointerMotionMask,
            GrabModeAsync, GrabModeAsync, x_snapit.win, None, CurrentTime);
        if (r == GrabSuccess) break;
        drain_events();
        usleep(10000);
    }
    XGrabKeyboard(x_dpy, x_snapit.win, True,
        GrabModeAsync, GrabModeAsync, CurrentTime);

    XGCValues dgcv;
    dgcv.foreground = WhitePixel(x_dpy, x_screen);
    dgcv.line_style = LineOnOffDash;
    dgcv.line_width = 1;
    x_snapit.dash_gc = XCreateGC(x_dpy, x_snapit.back_pm,
        GCForeground | GCLineStyle | GCLineWidth, &dgcv);
    x_snapit.dashes[0] = 5;
    x_snapit.dashes[1] = 5;
    XSetDashes(x_dpy, x_snapit.dash_gc, 0, x_snapit.dashes, 2);

    x_snapit.has_prev = 0;
    x_snapit.local_dash = 0;
    {
        struct timespec ts;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        x_snapit.last_anim_ns = (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec;
    }
    snapit.configured = 1;
}

static void x11_close_snapit(void)
{
    if (x_snapit.created) {
        XUngrabKeyboard(x_dpy, CurrentTime);
        XUngrabPointer(x_dpy, CurrentTime);
    }
    if (x_snapit.back_pic) { XRenderFreePicture(x_dpy, x_snapit.back_pic); x_snapit.back_pic = 0; }
    if (x_snapit.copy_gc) { XFreeGC(x_dpy, x_snapit.copy_gc); x_snapit.copy_gc = NULL; }
    if (x_snapit.dash_gc) { XFreeGC(x_dpy, x_snapit.dash_gc); x_snapit.dash_gc = NULL; }
    if (x_snapit.created) { XDestroyWindow(x_dpy, x_snapit.win); x_snapit.created = 0; }
    if (x_snapit.back_pm) { XFreePixmap(x_dpy, x_snapit.back_pm); x_snapit.back_pm = 0; }
    if (x_snapit.bg_pm) { XFreePixmap(x_dpy, x_snapit.bg_pm); x_snapit.bg_pm = 0; }
    if (snapit.hint_cs) { cairo_surface_destroy(snapit.hint_cs); snapit.hint_cs = NULL; }
    x_snapit.has_prev = 0;
}

static void x11_snapit_redraw(void)
{
    if (!x_snapit.created || !snapit.configured) return;

    int sw = snapit.surf_w, sh = snapit.surf_h;

    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    long long now_ns = (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec;
    int dash_changed = 0;
    if (now_ns - x_snapit.last_anim_ns >= 50000000LL) {
        x_snapit.local_dash = (x_snapit.local_dash + 1) % 10;
        x_snapit.last_anim_ns = now_ns;
        dash_changed = 1;
    }
    if (!snapit.dragging) {
        if (x_snapit.has_prev) {
            int pad = 2;
            int dx = x_snapit.prev_rx - pad, dy = x_snapit.prev_ry - pad;
            int dw = (int)x_snapit.prev_rw + 2 * pad;
            int dh = (int)x_snapit.prev_rh + 2 * pad;
            if (dx < 0) { dw += dx; dx = 0; }
            if (dy < 0) { dh += dy; dy = 0; }
            if (dx + dw > sw) dw = sw - dx;
            if (dy + dh > sh) dh = sh - dy;
            if (dw > 0 && dh > 0) {
                XCopyArea(x_dpy, x_snapit.bg_pm, x_snapit.back_pm,
                    x_snapit.copy_gc, dx, dy, dw, dh, dx, dy);
                XCopyArea(x_dpy, x_snapit.back_pm, x_snapit.win,
                    x_snapit.copy_gc, dx, dy, dw, dh, dx, dy);
            }
            x_snapit.has_prev = 0;
        }
        return;
    }

    int rx, ry;
    unsigned int rw, rh;
    double sx = snapit.start_x, sy = snapit.start_y;
    double cx = snapit.cur_x, cy = snapit.cur_y;
    if (sx < cx) { rx = (int)sx; rw = (unsigned)(cx - sx); }
    else         { rx = (int)cx; rw = (unsigned)(sx - cx); }
    if (sy < cy) { ry = (int)sy; rh = (unsigned)(cy - sy); }
    else         { ry = (int)cy; rh = (unsigned)(sy - cy); }

    if (rw < 2 || rh < 2) return;

    if (x_snapit.has_prev &&
        rx == x_snapit.prev_rx && ry == x_snapit.prev_ry &&
        rw == x_snapit.prev_rw && rh == x_snapit.prev_rh &&
        !dash_changed)
        return;

    XSetDashes(x_dpy, x_snapit.dash_gc, x_snapit.local_dash,
        x_snapit.dashes, 2);

    int pad = 2;
    int dx, dy, dw, dh;
    if (!x_snapit.has_prev) {
        dx = rx - pad;  dy = ry - pad;
        dw = (int)rw + 2 * pad;  dh = (int)rh + 2 * pad;
    } else {
        int px1 = x_snapit.prev_rx + (int)x_snapit.prev_rw;
        int py1 = x_snapit.prev_ry + (int)x_snapit.prev_rh;
        int cx1 = rx + (int)rw, cy1 = ry + (int)rh;
        dx = (x_snapit.prev_rx < rx ? x_snapit.prev_rx : rx) - pad;
        dy = (x_snapit.prev_ry < ry ? x_snapit.prev_ry : ry) - pad;
        dw = ((px1 > cx1 ? px1 : cx1) + pad) - dx;
        dh = ((py1 > cy1 ? py1 : cy1) + pad) - dy;
    }
    if (dx < 0) { dw += dx; dx = 0; }
    if (dy < 0) { dh += dy; dy = 0; }
    if (dx + dw > sw) dw = sw - dx;
    if (dy + dh > sh) dh = sh - dy;
    if (dw <= 0 || dh <= 0) return;

    XCopyArea(x_dpy, x_snapit.bg_pm, x_snapit.back_pm,
        x_snapit.copy_gc, dx, dy, dw, dh, dx, dy);

    XRenderColor fill = { 0x2555, 0x2555, 0x2555, 0x2555 };
    XRenderFillRectangle(x_dpy, PictOpOver, x_snapit.back_pic, &fill,
        rx, ry, rw, rh);

    XDrawRectangle(x_dpy, x_snapit.back_pm, x_snapit.dash_gc,
        rx, ry, rw - 1, rh - 1);

    XCopyArea(x_dpy, x_snapit.back_pm, x_snapit.win,
        x_snapit.copy_gc, dx, dy, dw, dh, dx, dy);

    x_snapit.prev_rx = rx;  x_snapit.prev_ry = ry;
    x_snapit.prev_rw = rw;  x_snapit.prev_rh = rh;
    x_snapit.has_prev = 1;
}

static void x11_rw_tick(void)
{
    if (!x_rw.created || !rw.visible || !rw.configured)
        return;

    check_evdev_hotplug();

    int have_ptr = 0;
    int rx = 0, ry = 0;
    unsigned int mask = 0;

    if (qp_pending) {
        xcb_query_pointer_reply_t *r =
            xcb_query_pointer_reply(xcb_conn, qp_cookie, NULL);
        if (r) {
            rx = r->root_x;
            ry = r->root_y;
            mask = r->mask;
            last_ptr_x = rx;
            last_ptr_y = ry;
            free(r);
            have_ptr = 1;
        }
        qp_pending = 0;
    }

    {
        static long long last_raise_ns;
        struct timespec ts;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        long long now = (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec;
        if (now - last_raise_ns > 2000000000LL) {
            for (int i = 0; i < MAX_PANELS; i++)
                if (x_panels[i].created && panels[i].visible)
                    raise_window(x_panels[i].win);
            raise_window(x_rw.win);
            last_raise_ns = now;
        }
    }

    if (evdev_fd >= 0 && have_ptr) {
        struct input_event ie;
        ssize_t n;
        while ((n = read(evdev_fd, &ie, sizeof(ie))) == (ssize_t)sizeof(ie)) {
            if (ie.type != EV_KEY || ie.code != BTN_LEFT) continue;
            if (!rw.visible || !rw.configured) break;
            int over = (rx >= rw.offset_x && rx < rw.offset_x + rw.total_w &&
                        ry >= rw.offset_y && ry < rw.offset_y + RW_TOTAL_H);
            if (ie.value == 1 && over && !rw.dragging) {
                pointer_on_rw = 1;
                handle_rw_button_press(rx, ry);
            } else if (ie.value == 0 && rw.dragging) {
                handle_rw_button_release();
            }
        }
        if (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK) {
            fprintf(stderr, "wfinfo-overlay: evdev mouse disconnected\n");
            close(evdev_fd);
            evdev_fd = -1;
        }
    }

    if (have_ptr) {
        int over = (rx >= rw.offset_x && rx < rw.offset_x + rw.total_w &&
                    ry >= rw.offset_y && ry < rw.offset_y + RW_TOTAL_H);
        pointer_on_rw = over || rw.dragging;

        int btn_now = (mask & XCB_BUTTON_MASK_1) != 0;
        if (btn_now && !x11_btn_prev) {
            if (over && !rw.dragging) {
                pointer_on_rw = 1;
                handle_rw_button_press(rx, ry);
            }
        } else if (!btn_now && x11_btn_prev) {
            if (rw.dragging)
                handle_rw_button_release();
        }
        x11_btn_prev = btn_now;

        if (rw.dragging) {
            int new_ox = rw.drag_start_ox + (rx - (int)rw.drag_start_px);
            int new_oy = rw.drag_start_oy + (ry - (int)rw.drag_start_py);
            if (new_ox != rw.offset_x || new_oy != rw.offset_y) {
                rw.offset_x = new_ox;
                rw.offset_y = new_oy;
                rw_ptr_x = rx;
                rw_ptr_y = ry;
                XMoveWindow(x_dpy, x_rw.win, rw.offset_x, rw.offset_y);
            }
        }

        int lx = rx - rw.offset_x;
        int ly = ry - rw.offset_y;
        int want_cursor = (over || rw.dragging) && x_rw.win_pic && x_rw.content_pic;
        if (want_cursor && !cursor_pic) create_cursor_server();

        int need_update = (want_cursor && (!cursor_on || lx != cursor_lx || ly != cursor_ly))
                        || (!want_cursor && cursor_on);

        if (need_update) {
            if (cursor_on)
                XRenderComposite(x_dpy, PictOpSrc,
                    x_rw.content_pic, None, x_rw.win_pic,
                    cursor_lx, cursor_ly, 0, 0, cursor_lx, cursor_ly,
                    CURSOR_W, CURSOR_H);
            if (want_cursor) {
                XRenderComposite(x_dpy, PictOpOver,
                    cursor_pic, None, x_rw.win_pic,
                    0, 0, 0, 0, lx, ly, CURSOR_W, CURSOR_H);
                cursor_lx = lx;
                cursor_ly = ly;
                cursor_on = 1;
            } else {
                cursor_on = 0;
            }
        }
    }

    qp_cookie = xcb_query_pointer(xcb_conn, (xcb_window_t)x_root);
    qp_pending = 1;
    xcb_flush(xcb_conn);
}

static OverlayBackend x11_backend = {
    .init             = x11_init,
    .destroy          = x11_destroy,
    .get_fd           = x11_get_fd,
    .dispatch         = x11_dispatch,
    .flush            = x11_flush,
    .show_panel       = x11_show_panel,
    .hide_panel       = x11_hide_panel,
    .rerender_panel   = x11_rerender_panel,
    .show_rw          = x11_show_rw,
    .hide_rw          = x11_hide_rw,
    .rw_redraw        = x11_rw_redraw,
    .rw_set_input_region = x11_rw_set_input_region,
    .start_snapit     = x11_start_snapit,
    .close_snapit     = x11_close_snapit,
    .snapit_redraw    = x11_snapit_redraw,
    .rw_tick          = x11_rw_tick,
};

OverlayBackend *x11_backend_create(void)
{
    return &x11_backend;
}
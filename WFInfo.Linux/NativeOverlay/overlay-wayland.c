#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/mman.h>
#include <wayland-client.h>
#include <wayland-cursor.h>
#include <cairo/cairo.h>
#include <X11/Xlib.h>
#include <X11/Xlib-xcb.h>
#include <xcb/xcb.h>
#include "wlr-layer-shell-client.h"
#include "overlay.h"

/* ---- Wayland globals ---- */

typedef struct OutputInfo {
    uint32_t           global_name;
    struct wl_output  *wl_output;
    int32_t            x, y;
    int32_t            phys_w, phys_h;
    int32_t            width, height;
    int32_t            scale;
    int                done;
    struct OutputInfo *next;
} OutputInfo;

static OutputInfo *output_list;

static struct wl_display    *wl_dpy;
static struct wl_compositor *compositor;
static struct wl_shm        *shm;
static struct zwlr_layer_shell_v1 *layer_shell;
static struct wl_seat       *seat;
static struct wl_pointer    *wl_pointer;
static struct wl_keyboard   *wl_keyboard;

static struct wl_cursor_theme *cursor_theme;
static struct wl_cursor      *cursor_cross;
static struct wl_surface     *cursor_surface;
/* ---- X11 aux for pointer polling (bypasses Wayland focus model) ---- */

static Display *x_dpy_aux;
static xcb_connection_t *xcb_conn_aux;
static xcb_window_t xcb_root_aux;
static xcb_query_pointer_cookie_t qp_cookie;
static int qp_pending;
static int wl_has_pointer_focus;

static OutputInfo *get_default_output(void);
static OutputInfo *find_output_at_point(int px, int py);

static int sw_cursor_show;
static int sw_cursor_x, sw_cursor_y;
static int wl_btn_prev;
static uint32_t wl_last_enter_serial;
static int wl_cursor_hidden;

/* ---- per-panel Wayland state ---- */

typedef struct {
    struct wl_surface            *surface;
    struct zwlr_layer_surface_v1 *layer_surface;
    struct wl_buffer             *buffer;
    void                         *buf_data;
    size_t                        buf_size;
    int                           active_scale;
} WlPanel;

static WlPanel wl_panels[MAX_PANELS];

/* ---- double-buffer frame ---- */

typedef struct {
    struct wl_buffer *wl_buf;
    void             *data;
    size_t            size;
    int               busy;
} WlFrameBuf;

static void cleanup_framebufs(WlFrameBuf bufs[2])
{
    for (int i = 0; i < 2; i++) {
        if (bufs[i].data) { munmap(bufs[i].data, bufs[i].size); bufs[i].data = NULL; }
        if (bufs[i].wl_buf) { wl_buffer_destroy(bufs[i].wl_buf); bufs[i].wl_buf = NULL; }
        bufs[i].busy = 0;
    }
}

/* ---- RW Wayland state ---- */

static struct {
    struct wl_surface            *surface;
    struct zwlr_layer_surface_v1 *layer_surface;
    WlFrameBuf                    bufs[2];
    int                           last_buf;
    unsigned char                *cache;
    int                           cache_size;
    int                           cache_valid;
    int                           active_scale;
    int                           output_ox, output_oy;
    int                           output_phys_w, output_phys_h;
    int                           output_logical_w, output_logical_h;
} wl_rw;

/* ---- SnapIt Wayland state ---- */

static struct {
    struct wl_surface            *surface;
    struct zwlr_layer_surface_v1 *layer_surface;
    WlFrameBuf                    bufs[2];
    int                           active_scale;
} wl_snapit;

/* ---- SHM buffer creation ---- */

static struct wl_buffer *create_shm_buffer(int w, int h, void **data_out, size_t *size_out)
{
    size_t stride = (size_t)w * 4;
    size_t size = stride * h;

    int fd = memfd_create("wfinfo-overlay", MFD_CLOEXEC);
    if (fd < 0) return NULL;
    if (ftruncate(fd, size) < 0) { close(fd); return NULL; }

    *data_out = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (*data_out == MAP_FAILED) { *data_out = NULL; close(fd); return NULL; }

    struct wl_shm_pool *pool = wl_shm_create_pool(shm, fd, size);
    struct wl_buffer *buf = wl_shm_pool_create_buffer(pool, 0, w, h, stride,
                                                       WL_SHM_FORMAT_ARGB8888);
    wl_shm_pool_destroy(pool);
    close(fd);
    *size_out = size;
    return buf;
}

/* ---- framebuf release callback ---- */

static void framebuf_release(void *data, struct wl_buffer *buffer)
{
    (void)buffer;
    WlFrameBuf *fb = data;
    fb->busy = 0;
}

static const struct wl_buffer_listener framebuf_listener = {
    .release = framebuf_release,
};

/* ---- panel cleanup helpers ---- */

static void cleanup_panel_buffer(WlPanel *wp)
{
    if (wp->buf_data && wp->buf_size > 0) {
        munmap(wp->buf_data, wp->buf_size);
        wp->buf_data = NULL;
    }
    if (wp->buffer) {
        wl_buffer_destroy(wp->buffer);
        wp->buffer = NULL;
    }
}

static void cleanup_panel_resources(WlPanel *wp)
{
    if (wp->layer_surface) {
        zwlr_layer_surface_v1_destroy(wp->layer_surface);
        wp->layer_surface = NULL;
    }
    if (wp->surface) {
        wl_surface_destroy(wp->surface);
        wp->surface = NULL;
    }
    cleanup_panel_buffer(wp);
}

/* ---- panel layer surface callbacks ---- */

static void panel_configure(void *data,
    struct zwlr_layer_surface_v1 *ls, uint32_t serial, uint32_t w, uint32_t h)
{
    Panel *p = data;
    int id = (int)(p - panels);
    WlPanel *wp = &wl_panels[id];
    zwlr_layer_surface_v1_ack_configure(ls, serial);

    if (w > 0) p->w = (int)w;
    if (h > 0) p->h = (int)h;
    p->configured = 1;

    cleanup_panel_buffer(wp);

    int s = wp->active_scale > 0 ? wp->active_scale : 1;
    int bw = p->w * s, bh = p->h * s;
    wp->buffer = create_shm_buffer(bw, bh, &wp->buf_data, &wp->buf_size);
    if (!wp->buffer) return;

    render_panel(p, wp->buf_data, s);

    wl_surface_set_buffer_scale(wp->surface, s);
    wl_surface_attach(wp->surface, wp->buffer, 0, 0);
    wl_surface_damage_buffer(wp->surface, 0, 0, bw, bh);

    struct wl_region *region = wl_compositor_create_region(compositor);
    wl_surface_set_input_region(wp->surface, region);
    wl_region_destroy(region);

    wl_surface_commit(wp->surface);
}

static void panel_closed(void *data,
    struct zwlr_layer_surface_v1 *ls)
{
    (void)ls;
    Panel *p = data;
    int id = (int)(p - panels);
    cleanup_panel_resources(&wl_panels[id]);
    p->visible = 0;
    p->configured = 0;
    p->hide_at = 0;
}

static const struct zwlr_layer_surface_v1_listener panel_listener = {
    .configure = panel_configure,
    .closed    = panel_closed,
};

/* ---- RW callbacks ---- */

static void wl_rw_commit(void);

static void rw_configure(void *data,
    struct zwlr_layer_surface_v1 *ls, uint32_t serial, uint32_t w, uint32_t h)
{
    (void)data;
    zwlr_layer_surface_v1_ack_configure(ls, serial);
    rw.surf_w = (int)w;
    rw.surf_h = (int)h;

    cleanup_framebufs(wl_rw.bufs);
    wl_rw.cache_valid = 0;

    int s = wl_rw.active_scale > 0 ? wl_rw.active_scale : 1;
    int bw = rw.surf_w * s, bh = rw.surf_h * s;
    for (int i = 0; i < 2; i++) {
        wl_rw.bufs[i].wl_buf = create_shm_buffer(bw, bh,
            &wl_rw.bufs[i].data, &wl_rw.bufs[i].size);
        if (!wl_rw.bufs[i].wl_buf) return;
        wl_buffer_add_listener(wl_rw.bufs[i].wl_buf, &framebuf_listener, &wl_rw.bufs[i]);
    }

    rw.configured = 1;
    {
        struct wl_region *region = wl_compositor_create_region(compositor);
        wl_region_add(region, 0, 0, rw.surf_w, rw.surf_h);
        wl_surface_set_input_region(wl_rw.surface, region);
        wl_region_destroy(region);
    }
    wl_rw_commit();
}

static void rw_closed(void *data, struct zwlr_layer_surface_v1 *ls)
{
    (void)data; (void)ls;
    sw_cursor_show = 0;
    if (wl_rw.layer_surface) {
        zwlr_layer_surface_v1_destroy(wl_rw.layer_surface);
        wl_rw.layer_surface = NULL;
    }
    if (wl_rw.surface) {
        wl_surface_destroy(wl_rw.surface);
        wl_rw.surface = NULL;
    }
    cleanup_framebufs(wl_rw.bufs);
    free(wl_rw.cache); wl_rw.cache = NULL;
    wl_rw.cache_size = 0; wl_rw.cache_valid = 0;
    wl_has_pointer_focus = 0;
    pointer_on_rw = 0;
    rw.visible = 0;
    rw.configured = 0;
    rw.dragging = 0;
}

static const struct zwlr_layer_surface_v1_listener rw_layer_listener = {
    .configure = rw_configure,
    .closed    = rw_closed,
};

/* ---- SnapIt callbacks ---- */

static void wl_snapit_commit(void);

static void snapit_configure(void *data,
    struct zwlr_layer_surface_v1 *ls, uint32_t serial, uint32_t w, uint32_t h)
{
    (void)data;
    zwlr_layer_surface_v1_ack_configure(ls, serial);
    snapit.surf_w = (int)w;
    snapit.surf_h = (int)h;

    cleanup_framebufs(wl_snapit.bufs);

    int s = wl_snapit.active_scale > 0 ? wl_snapit.active_scale : 1;
    int bw = snapit.surf_w * s, bh = snapit.surf_h * s;
    for (int i = 0; i < 2; i++) {
        wl_snapit.bufs[i].wl_buf = create_shm_buffer(bw, bh,
            &wl_snapit.bufs[i].data, &wl_snapit.bufs[i].size);
        if (!wl_snapit.bufs[i].wl_buf) return;
        wl_buffer_add_listener(wl_snapit.bufs[i].wl_buf, &framebuf_listener, &wl_snapit.bufs[i]);
    }

    snapit_cache_hint();
    snapit.configured = 1;
    wl_snapit_commit();
}

static void snapit_closed(void *data, struct zwlr_layer_surface_v1 *ls)
{
    (void)data; (void)ls;
    printf("{\"event\":\"snapit_cancel\"}\n");
    fflush(stdout);
    if (wl_snapit.layer_surface) { zwlr_layer_surface_v1_destroy(wl_snapit.layer_surface); wl_snapit.layer_surface = NULL; }
    if (wl_snapit.surface) { wl_surface_destroy(wl_snapit.surface); wl_snapit.surface = NULL; }
    cleanup_framebufs(wl_snapit.bufs);
    if (snapit.hint_cs) { cairo_surface_destroy(snapit.hint_cs); snapit.hint_cs = NULL; }
    snapit.active = 0;
    snapit.configured = 0;
    snapit.dragging = 0;
}

static const struct zwlr_layer_surface_v1_listener snapit_layer_listener = {
    .configure = snapit_configure,
    .closed    = snapit_closed,
};

/* ---- input listeners ---- */

static void pointer_enter(void *data, struct wl_pointer *p, uint32_t serial,
    struct wl_surface *surface, wl_fixed_t sx, wl_fixed_t sy)
{
    (void)data;

    if (snapit.active && surface == wl_snapit.surface) {
        if (cursor_cross && cursor_surface) {
            int cs = wl_snapit.active_scale > 0 ? wl_snapit.active_scale : 1;
            struct wl_cursor_image *img = cursor_cross->images[0];
            struct wl_buffer *cbuf = wl_cursor_image_get_buffer(img);
            wl_surface_attach(cursor_surface, cbuf, 0, 0);
            wl_surface_set_buffer_scale(cursor_surface, cs);
            wl_surface_damage(cursor_surface, 0, 0, img->width, img->height);
            wl_surface_commit(cursor_surface);
            wl_pointer_set_cursor(p, serial, cursor_surface,
                img->hotspot_x / cs,
                img->hotspot_y / cs);
        }
    }

    if (rw.visible && surface == wl_rw.surface) {
        wl_has_pointer_focus = 1;
        pointer_on_rw = 1;
        wl_last_enter_serial = serial;
        rw_ptr_x = rw.offset_x + wl_fixed_to_double(sx);
        rw_ptr_y = rw.offset_y + wl_fixed_to_double(sy);
        wl_pointer_set_cursor(p, serial, NULL, 0, 0);
        wl_cursor_hidden = 1;
        sw_cursor_x = (int)wl_fixed_to_double(sx);
        sw_cursor_y = (int)wl_fixed_to_double(sy);
        if (!sw_cursor_show) {
            sw_cursor_show = 1;
            wl_rw_commit();
        }
    }
}

static void pointer_leave(void *data, struct wl_pointer *p, uint32_t serial,
    struct wl_surface *surface)
{
    (void)data; (void)p; (void)serial;
    if (rw.visible && surface == wl_rw.surface) {
        wl_has_pointer_focus = 0;
        wl_cursor_hidden = 0;
        pointer_on_rw = 0;
        if (sw_cursor_show) {
            sw_cursor_show = 0;
            wl_rw_commit();
        }
    }
}

static void pointer_motion(void *data, struct wl_pointer *p, uint32_t time,
    wl_fixed_t sx, wl_fixed_t sy)
{
    (void)data; (void)p; (void)time;

    if (pointer_on_rw && rw.dragging) {
        handle_rw_motion(rw.offset_x + wl_fixed_to_double(sx),
                         rw.offset_y + wl_fixed_to_double(sy));
        return;
    }

    if (pointer_on_rw) {
        handle_rw_motion(rw.offset_x + wl_fixed_to_double(sx),
                         rw.offset_y + wl_fixed_to_double(sy));
        int ncx = (int)wl_fixed_to_double(sx);
        int ncy = (int)wl_fixed_to_double(sy);
        if (ncx != sw_cursor_x || ncy != sw_cursor_y) {
            sw_cursor_x = ncx;
            sw_cursor_y = ncy;
            sw_cursor_show = 1;
            wl_rw_commit();
        }
        return;
    }

    if (!snapit.active) return;
    handle_snapit_motion(wl_fixed_to_double(sx), wl_fixed_to_double(sy));
    if (snapit.dragging && snapit.configured)
        wl_snapit_commit();
}

static void pointer_button(void *data, struct wl_pointer *p, uint32_t serial,
    uint32_t time, uint32_t button, uint32_t state)
{
    (void)data; (void)p; (void)serial; (void)time;

    if (pointer_on_rw && button == 0x110) {
        if (state == WL_POINTER_BUTTON_STATE_PRESSED)
            handle_rw_button_press(rw_ptr_x, rw_ptr_y);
        else if (state == WL_POINTER_BUTTON_STATE_RELEASED)
            handle_rw_button_release();
        return;
    }

    if (!snapit.active) return;

    if (button == 0x110) {
        if (state == WL_POINTER_BUTTON_STATE_PRESSED)
            handle_snapit_press(snapit.cur_x, snapit.cur_y);
        else if (state == WL_POINTER_BUTTON_STATE_RELEASED)
            handle_snapit_release(snapit.cur_x, snapit.cur_y);
    }
}

static void pointer_axis(void *data, struct wl_pointer *p, uint32_t time,
    uint32_t axis, wl_fixed_t value)
{
    (void)data; (void)p; (void)time; (void)axis; (void)value;
}

static const struct wl_pointer_listener wl_pointer_listener = {
    .enter  = pointer_enter,
    .leave  = pointer_leave,
    .motion = pointer_motion,
    .button = pointer_button,
    .axis   = pointer_axis,
};

static void keyboard_keymap(void *data, struct wl_keyboard *kb,
    uint32_t format, int fd, uint32_t size)
{
    (void)data; (void)kb; (void)format; (void)size;
    close(fd);
}

static void keyboard_enter(void *data, struct wl_keyboard *kb, uint32_t serial,
    struct wl_surface *surface, struct wl_array *keys)
{
    (void)data; (void)kb; (void)serial; (void)surface; (void)keys;
}

static void keyboard_leave(void *data, struct wl_keyboard *kb, uint32_t serial,
    struct wl_surface *surface)
{
    (void)data; (void)kb; (void)serial; (void)surface;
}

static void keyboard_key(void *data, struct wl_keyboard *kb, uint32_t serial,
    uint32_t time, uint32_t key, uint32_t state)
{
    (void)data; (void)kb; (void)serial; (void)time; (void)key; (void)state;
}

static void keyboard_modifiers(void *data, struct wl_keyboard *kb,
    uint32_t serial, uint32_t mods_depressed, uint32_t mods_latched,
    uint32_t mods_locked, uint32_t group)
{
    (void)data; (void)kb; (void)serial;
    (void)mods_depressed; (void)mods_latched; (void)mods_locked; (void)group;
}

static const struct wl_keyboard_listener wl_keyboard_listener = {
    .keymap    = keyboard_keymap,
    .enter     = keyboard_enter,
    .leave     = keyboard_leave,
    .key       = keyboard_key,
    .modifiers = keyboard_modifiers,
};

static void seat_capabilities(void *data, struct wl_seat *s, uint32_t caps)
{
    (void)data; (void)s;
    if ((caps & WL_SEAT_CAPABILITY_POINTER) && !wl_pointer) {
        wl_pointer = wl_seat_get_pointer(seat);
        wl_pointer_add_listener(wl_pointer, &wl_pointer_listener, NULL);

        if (!cursor_theme && shm) {
            int csize = 24;
            const char *csize_env = getenv("XCURSOR_SIZE");
            if (csize_env && csize_env[0]) { int v = atoi(csize_env); if (v > 0) csize = v; }
            OutputInfo *def = get_default_output();
            int cscale = (def && def->scale > 0) ? def->scale : 1;
            cursor_theme = wl_cursor_theme_load(NULL, csize * cscale, shm);
            if (cursor_theme) {
                cursor_cross = wl_cursor_theme_get_cursor(cursor_theme, "crosshair");
                if (!cursor_cross)
                    cursor_cross = wl_cursor_theme_get_cursor(cursor_theme, "cross");
                if (!cursor_cross)
                    cursor_cross = wl_cursor_theme_get_cursor(cursor_theme, "left_ptr");
                cursor_surface = wl_compositor_create_surface(compositor);
            }
        }
    }
    if ((caps & WL_SEAT_CAPABILITY_KEYBOARD) && !wl_keyboard) {
        wl_keyboard = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(wl_keyboard, &wl_keyboard_listener, NULL);
    }
}

static void seat_name(void *data, struct wl_seat *s, const char *name)
{
    (void)data; (void)s; (void)name;
}

static const struct wl_seat_listener wl_seat_listener = {
    .capabilities = seat_capabilities,
    .name         = seat_name,
};

/* ---- output listener ---- */

static void output_geometry(void *data, struct wl_output *o,
    int32_t x, int32_t y, int32_t pw, int32_t ph,
    int32_t subpixel, const char *make, const char *model, int32_t transform)
{
    (void)o; (void)pw; (void)ph; (void)subpixel; (void)make; (void)model; (void)transform;
    OutputInfo *oi = data;
    if (oi) { oi->x = x; oi->y = y; }
}

static void output_mode(void *data, struct wl_output *o,
    uint32_t flags, int32_t w, int32_t h, int32_t refresh)
{
    (void)o; (void)refresh;
    if (flags & WL_OUTPUT_MODE_CURRENT) {
        OutputInfo *oi = data;
        if (oi) { oi->phys_w = w; oi->phys_h = h; }
    }
}

static void output_done(void *data, struct wl_output *o)
{
    (void)o;
    OutputInfo *oi = data;
    if (!oi) return;
    int s = oi->scale > 0 ? oi->scale : 1;
    oi->width = oi->phys_w / s;
    oi->height = oi->phys_h / s;
    oi->done = 1;
    snapit_output_scale = s;
    fprintf(stderr, "wfinfo-overlay: wl_output(%u): %dx%d physical, scale=%d, logical=%dx%d at (%d,%d)\n",
            oi->global_name, oi->phys_w, oi->phys_h, s, oi->width, oi->height, oi->x, oi->y);
}

static void output_scale(void *data, struct wl_output *o, int32_t factor)
{
    (void)o;
    OutputInfo *oi = data;
    if (oi && factor >= 1) oi->scale = factor;
}

static const struct wl_output_listener output_listener = {
    .geometry = output_geometry,
    .mode     = output_mode,
    .done     = output_done,
    .scale    = output_scale,
};

/* ---- registry ---- */

static void registry_global(void *data, struct wl_registry *reg,
    uint32_t name, const char *interface, uint32_t version)
{
    (void)data; (void)version;
    if (strcmp(interface, wl_compositor_interface.name) == 0)
        compositor = wl_registry_bind(reg, name, &wl_compositor_interface, 4);
    else if (strcmp(interface, wl_shm_interface.name) == 0)
        shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    else if (strcmp(interface, zwlr_layer_shell_v1_interface.name) == 0) {
        uint32_t bind_ver = version < 4 ? version : 4;
        layer_shell = wl_registry_bind(reg, name, &zwlr_layer_shell_v1_interface, bind_ver);
    }
    else if (strcmp(interface, wl_seat_interface.name) == 0 && !seat) {
        seat = wl_registry_bind(reg, name, &wl_seat_interface, 1);
        wl_seat_add_listener(seat, &wl_seat_listener, NULL);
    }
    else if (strcmp(interface, wl_output_interface.name) == 0) {
        OutputInfo *oi = calloc(1, sizeof(OutputInfo));
        if (!oi) return;
        oi->global_name = name;
        oi->scale = 1;
        uint32_t bind_ver = version < 2 ? version : 2;
        oi->wl_output = wl_registry_bind(reg, name, &wl_output_interface, bind_ver);
        wl_output_add_listener(oi->wl_output, &output_listener, oi);
        oi->next = output_list;
        output_list = oi;
    }
}

static void registry_global_remove(void *data, struct wl_registry *reg, uint32_t name)
{
    (void)data; (void)reg;
    OutputInfo **pp = &output_list;
    while (*pp) {
        if ((*pp)->global_name == name) {
            OutputInfo *oi = *pp;
            *pp = oi->next;
            wl_output_destroy(oi->wl_output);
            free(oi);
            return;
        }
        pp = &(*pp)->next;
    }
}

static const struct wl_registry_listener registry_listener = {
    .global        = registry_global,
    .global_remove = registry_global_remove,
};

static OutputInfo *get_default_output(void)
{
    for (OutputInfo *oi = output_list; oi; oi = oi->next)
        if (oi->done) return oi;
    return output_list;
}

static OutputInfo *find_output_at_point(int px, int py)
{
    for (OutputInfo *oi = output_list; oi; oi = oi->next) {
        if (!oi->done) continue;
        if (px >= oi->x && px < oi->x + oi->width &&
            py >= oi->y && py < oi->y + oi->height)
            return oi;
    }
    return get_default_output();
}

/* ---- output logical-size probe ---- */

static void probe_configure(void *data, struct zwlr_layer_surface_v1 *ls,
    uint32_t serial, uint32_t w, uint32_t h)
{
    zwlr_layer_surface_v1_ack_configure(ls, serial);
    int *dims = data;
    dims[0] = (int)w;
    dims[1] = (int)h;
}

static void probe_closed(void *data, struct zwlr_layer_surface_v1 *ls)
{ (void)data; (void)ls; }

static const struct zwlr_layer_surface_v1_listener probe_listener = {
    .configure = probe_configure,
    .closed    = probe_closed,
};

static void probe_output_logical_size(OutputInfo *oi)
{
    if (!compositor || !layer_shell || !oi->wl_output) return;

    struct wl_surface *surf = wl_compositor_create_surface(compositor);
    if (!surf) return;
    struct zwlr_layer_surface_v1 *ls = zwlr_layer_shell_v1_get_layer_surface(
        layer_shell, surf, oi->wl_output,
        ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND, "wfinfo-probe");
    if (!ls) { wl_surface_destroy(surf); return; }

    int dims[2] = {0, 0};
    zwlr_layer_surface_v1_set_anchor(ls,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
        ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_size(ls, 0, 0);
    zwlr_layer_surface_v1_set_exclusive_zone(ls, -1);
    zwlr_layer_surface_v1_add_listener(ls, &probe_listener, dims);
    wl_surface_commit(surf);
    wl_display_roundtrip(wl_dpy);

    if (dims[0] > 0 && dims[1] > 0) {
        int old_w = oi->width, old_h = oi->height;
        oi->width = dims[0];
        oi->height = dims[1];
        if (old_w != dims[0] || old_h != dims[1])
            fprintf(stderr, "wfinfo-overlay: output(%u) actual logical=%dx%d (was %dx%d)\n",
                    oi->global_name, dims[0], dims[1], old_w, old_h);
    }

    zwlr_layer_surface_v1_destroy(ls);
    wl_surface_destroy(surf);
}

/* ---- backend vtable implementation ---- */

static int wl_init(void)
{
    wl_dpy = wl_display_connect(NULL);
    if (!wl_dpy) return -1;

    struct wl_registry *reg = wl_display_get_registry(wl_dpy);
    wl_registry_add_listener(reg, &registry_listener, NULL);
    wl_display_roundtrip(wl_dpy);
    wl_display_roundtrip(wl_dpy);

    if (!compositor || !shm || !layer_shell) {
        fprintf(stderr, "wfinfo-overlay: missing Wayland globals "
                "(compositor=%p shm=%p layer_shell=%p)\n",
                (void*)compositor, (void*)shm, (void*)layer_shell);
        wl_display_disconnect(wl_dpy);
        wl_dpy = NULL;
        return -1;
    }

    x_dpy_aux = XOpenDisplay(NULL);
    if (!x_dpy_aux) {
        fprintf(stderr, "wfinfo-overlay: XWayland not available - pure Wayland unsupported\n");
        wl_display_disconnect(wl_dpy);
        wl_dpy = NULL;
        return -1;
    }
    xcb_conn_aux = XGetXCBConnection(x_dpy_aux);
    xcb_root_aux = xcb_setup_roots_iterator(
        xcb_get_setup(xcb_conn_aux)).data->root;
    if (!get_default_output()) {
        xcb_screen_t *scr = xcb_setup_roots_iterator(
            xcb_get_setup(xcb_conn_aux)).data;
        OutputInfo *oi = calloc(1, sizeof(OutputInfo));
        if (oi) {
            oi->scale = 1;
            oi->phys_w = scr->width_in_pixels;
            oi->phys_h = scr->height_in_pixels;
            oi->width = oi->phys_w;
            oi->height = oi->phys_h;
            oi->done = 1;
            oi->next = output_list;
            output_list = oi;
        }
    }

    for (OutputInfo *oi = output_list; oi; oi = oi->next) {
        if (oi->done && oi->wl_output)
            probe_output_logical_size(oi);
    }

    return 0;
}

static void wl_destroy(void)
{
    if (wl_snapit.layer_surface) zwlr_layer_surface_v1_destroy(wl_snapit.layer_surface);
    if (wl_snapit.surface) wl_surface_destroy(wl_snapit.surface);
    cleanup_framebufs(wl_snapit.bufs);
    if (snapit.hint_cs) cairo_surface_destroy(snapit.hint_cs);

    if (wl_rw.layer_surface) zwlr_layer_surface_v1_destroy(wl_rw.layer_surface);
    if (wl_rw.surface) wl_surface_destroy(wl_rw.surface);
    cleanup_framebufs(wl_rw.bufs);
    free(wl_rw.cache);

    for (int i = 0; i < MAX_PANELS; i++)
        cleanup_panel_resources(&wl_panels[i]);

    if (cursor_surface) wl_surface_destroy(cursor_surface);
    if (cursor_theme) wl_cursor_theme_destroy(cursor_theme);
    if (wl_pointer) wl_pointer_destroy(wl_pointer);
    if (wl_keyboard) wl_keyboard_destroy(wl_keyboard);
    if (seat) wl_seat_destroy(seat);
    while (output_list) {
        OutputInfo *oi = output_list;
        output_list = oi->next;
        if (oi->wl_output) wl_output_destroy(oi->wl_output);
        free(oi);
    }
    if (wl_dpy) wl_display_disconnect(wl_dpy);

    if (qp_pending && xcb_conn_aux) {
        free(xcb_query_pointer_reply(xcb_conn_aux, qp_cookie, NULL));
        qp_pending = 0;
    }
    if (x_dpy_aux) { XCloseDisplay(x_dpy_aux); x_dpy_aux = NULL; }
}

static int wl_get_fd(void) { return wl_display_get_fd(wl_dpy); }
static int wl_dispatch(void) { return wl_display_dispatch(wl_dpy); }
static void wl_flush(void) { wl_display_flush(wl_dpy); }

static void wl_show_panel(int id)
{
    Panel *p = &panels[id];
    WlPanel *wp = &wl_panels[id];

    cleanup_panel_resources(wp);

    OutputInfo *oi = find_output_at_point(p->x, p->y);
    struct wl_output *target = oi ? oi->wl_output : NULL;
    int ox = oi ? oi->x : 0, oy = oi ? oi->y : 0;
    wp->active_scale = oi ? (oi->scale > 0 ? oi->scale : 1) : 1;

    wp->surface = wl_compositor_create_surface(compositor);
    wp->layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer_shell, wp->surface, target,
        ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY, "wfinfo");

    zwlr_layer_surface_v1_set_size(wp->layer_surface, p->w, p->h);
    zwlr_layer_surface_v1_set_anchor(wp->layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);
    zwlr_layer_surface_v1_set_margin(wp->layer_surface, p->y - oy, 0, 0, p->x - ox);
    zwlr_layer_surface_v1_set_keyboard_interactivity(wp->layer_surface,
        ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);
    zwlr_layer_surface_v1_set_exclusive_zone(wp->layer_surface, -1);

    zwlr_layer_surface_v1_add_listener(wp->layer_surface, &panel_listener, p);
    wl_surface_commit(wp->surface);
}

static void wl_hide_panel(int id)
{
    cleanup_panel_resources(&wl_panels[id]);
}

static void wl_rerender_panel(int id)
{
    Panel *p = &panels[id];
    WlPanel *wp = &wl_panels[id];
    if (!wp->buf_data) return;
    int s = wp->active_scale > 0 ? wp->active_scale : 1;
    memset(wp->buf_data, 0, wp->buf_size);
    render_panel(p, wp->buf_data, s);
    wl_surface_attach(wp->surface, wp->buffer, 0, 0);
    wl_surface_damage_buffer(wp->surface, 0, 0, p->w * s, p->h * s);
    wl_surface_commit(wp->surface);
}

static void draw_sw_cursor(cairo_t *cr, double x, double y)
{
    cairo_save(cr);
    cairo_translate(cr, x, y);
    cairo_new_path(cr);
    cairo_move_to(cr, 0, 0);
    cairo_line_to(cr, 0, 17);
    cairo_line_to(cr, 4, 12);
    cairo_line_to(cr, 8, 20);
    cairo_line_to(cr, 11, 18);
    cairo_line_to(cr, 7, 11);
    cairo_line_to(cr, 12, 11);
    cairo_close_path(cr);
    cairo_set_source_rgb(cr, 1, 1, 1);
    cairo_fill_preserve(cr);
    cairo_set_source_rgb(cr, 0, 0, 0);
    cairo_set_line_width(cr, 1.2);
    cairo_stroke(cr);
    cairo_restore(cr);
}

static void wl_show_rw(void)
{
    rw.configured = 0;
    if (wl_rw.layer_surface) { zwlr_layer_surface_v1_destroy(wl_rw.layer_surface); wl_rw.layer_surface = NULL; }
    if (wl_rw.surface) { wl_surface_destroy(wl_rw.surface); wl_rw.surface = NULL; }
    cleanup_framebufs(wl_rw.bufs);
    free(wl_rw.cache); wl_rw.cache = NULL; wl_rw.cache_size = 0; wl_rw.cache_valid = 0;

    OutputInfo *oi = NULL;
    if (!rw.dragging && !rw.visible) {
        xcb_query_pointer_cookie_t qc = xcb_query_pointer(xcb_conn_aux, xcb_root_aux);
        xcb_query_pointer_reply_t *qr = xcb_query_pointer_reply(xcb_conn_aux, qc, NULL);
        int px = 0, py = 0;
        if (qr) { px = qr->root_x; py = qr->root_y; free(qr); }
        MonitorRect mon;
        get_monitor_at_point(x_dpy_aux, px, py, &mon);
        oi = find_output_at_point(mon.x, mon.y);
        int ow = oi ? oi->width : mon.w;
        int oh = oi ? oi->height : mon.h;
        int oox = oi ? oi->x : 0, ooy = oi ? oi->y : 0;
        rw.offset_x = oox + (ow - rw.total_w) / 2;
        rw.offset_y = ooy + (oh - RW_TOTAL_H) / 2;
        if (rw.offset_x < 0) rw.offset_x = 0;
        if (rw.offset_y < 0) rw.offset_y = 0;
    }
    if (!oi) oi = find_output_at_point(rw.offset_x, rw.offset_y);

    struct wl_output *target = oi ? oi->wl_output : NULL;
    int ox = oi ? oi->x : 0, oy = oi ? oi->y : 0;
    wl_rw.active_scale = oi ? (oi->scale > 0 ? oi->scale : 1) : 1;
    wl_rw.output_ox = ox;
    wl_rw.output_oy = oy;
    wl_rw.output_phys_w = oi ? oi->phys_w : 0;
    wl_rw.output_phys_h = oi ? oi->phys_h : 0;
    wl_rw.output_logical_w = oi ? oi->width : 0;
    wl_rw.output_logical_h = oi ? oi->height : 0;

    wl_rw.surface = wl_compositor_create_surface(compositor);
    wl_rw.layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer_shell, wl_rw.surface, target,
        ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY, "wfinfo-rewards");

    zwlr_layer_surface_v1_set_size(wl_rw.layer_surface, rw.total_w, RW_TOTAL_H);
    zwlr_layer_surface_v1_set_anchor(wl_rw.layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);
    zwlr_layer_surface_v1_set_margin(wl_rw.layer_surface,
        rw.offset_y - oy, 0, 0, rw.offset_x - ox);
    zwlr_layer_surface_v1_set_keyboard_interactivity(wl_rw.layer_surface,
        ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);
    zwlr_layer_surface_v1_set_exclusive_zone(wl_rw.layer_surface, -1);

    zwlr_layer_surface_v1_add_listener(wl_rw.layer_surface, &rw_layer_listener, NULL);
    wl_surface_commit(wl_rw.surface);
}

static void wl_rw_redraw(void)
{
    wl_rw.cache_valid = 0;
    wl_rw_commit();
}

static void wl_hide_rw(void)
{
    sw_cursor_show = 0;
    wl_has_pointer_focus = 0;
    wl_cursor_hidden = 0;
    wl_btn_prev = 0;
    pointer_on_rw = 0;
    if (wl_rw.layer_surface) {
        zwlr_layer_surface_v1_destroy(wl_rw.layer_surface);
        wl_rw.layer_surface = NULL;
    }
    if (wl_rw.surface) {
        wl_surface_destroy(wl_rw.surface);
        wl_rw.surface = NULL;
    }
    cleanup_framebufs(wl_rw.bufs);
    free(wl_rw.cache); wl_rw.cache = NULL;
    wl_rw.cache_size = 0; wl_rw.cache_valid = 0;
}

static void wl_rw_commit(void)
{
    if (!wl_rw.surface || !rw.configured || rw.surf_w <= 0 || rw.surf_h <= 0) return;

    if (rw.dragging) {
        if (!wl_rw.layer_surface) return;
        zwlr_layer_surface_v1_set_margin(wl_rw.layer_surface,
            rw.offset_y - wl_rw.output_oy, 0, 0, rw.offset_x - wl_rw.output_ox);
        wl_surface_commit(wl_rw.surface);
        return;
    }

    WlFrameBuf *fb = NULL;
    int idx = -1;
    for (int i = 0; i < 2; i++) {
        if (!wl_rw.bufs[i].busy) { fb = &wl_rw.bufs[i]; idx = i; break; }
    }
    if (!fb || !fb->wl_buf || !fb->data) return;

    int s = wl_rw.active_scale > 0 ? wl_rw.active_scale : 1;
    int bw = rw.surf_w * s, bh = rw.surf_h * s;
    int stride = bw * 4;
    int sz = stride * bh;

    if (!wl_rw.cache_valid) {
        if (wl_rw.cache_size != sz) {
            free(wl_rw.cache);
            wl_rw.cache = malloc(sz);
            if (!wl_rw.cache) { wl_rw.cache_size = 0; return; }
            wl_rw.cache_size = sz;
        }
        cairo_surface_t *ccs = cairo_image_surface_create_for_data(
            wl_rw.cache, CAIRO_FORMAT_ARGB32, bw, bh, stride);
        if (s > 1)
            cairo_surface_set_device_scale(ccs, s, s);
        cairo_t *ccr = cairo_create(ccs);
        cairo_set_operator(ccr, CAIRO_OPERATOR_SOURCE);
        cairo_set_source_rgba(ccr, 0.106, 0.106, 0.106, 1.0);
        cairo_paint(ccr);
        cairo_set_operator(ccr, CAIRO_OPERATOR_OVER);
        render_rw_content(ccr);
        cairo_destroy(ccr);
        cairo_surface_destroy(ccs);
        wl_rw.cache_valid = 1;
    }

    memcpy(fb->data, wl_rw.cache, sz);

    if (sw_cursor_show) {
        cairo_surface_t *cs = cairo_image_surface_create_for_data(
            fb->data, CAIRO_FORMAT_ARGB32, bw, bh, stride);
        if (s > 1)
            cairo_surface_set_device_scale(cs, s, s);
        cairo_t *cr = cairo_create(cs);
        draw_sw_cursor(cr, sw_cursor_x, sw_cursor_y);
        cairo_destroy(cr);
        cairo_surface_destroy(cs);
    }

    wl_rw.last_buf = idx;
    fb->busy = 1;
    wl_surface_set_buffer_scale(wl_rw.surface, s);
    wl_surface_attach(wl_rw.surface, fb->wl_buf, 0, 0);
    wl_surface_damage_buffer(wl_rw.surface, 0, 0, bw, bh);
    wl_surface_commit(wl_rw.surface);
}

static void wl_rw_set_input_region(int fullscreen)
{
    (void)fullscreen;
    if (!wl_rw.surface) return;
    struct wl_region *region = wl_compositor_create_region(compositor);
    wl_region_add(region, 0, 0, rw.surf_w, rw.surf_h);
    wl_surface_set_input_region(wl_rw.surface, region);
    wl_region_destroy(region);
    wl_surface_commit(wl_rw.surface);
}

static void wl_start_snapit(int req_w, int req_h)
{
    (void)req_w; (void)req_h;
    if (wl_snapit.layer_surface) { zwlr_layer_surface_v1_destroy(wl_snapit.layer_surface); wl_snapit.layer_surface = NULL; }
    if (wl_snapit.surface) { wl_surface_destroy(wl_snapit.surface); wl_snapit.surface = NULL; }
    cleanup_framebufs(wl_snapit.bufs);
    if (snapit.hint_cs) { cairo_surface_destroy(snapit.hint_cs); snapit.hint_cs = NULL; }

    snapit.dragging = 0;
    snapit.start_x = snapit.start_y = 0;
    snapit.cur_x = snapit.cur_y = 0;
    snapit.configured = 0;

    int px = 0, py = 0;
    {
        xcb_query_pointer_cookie_t qc = xcb_query_pointer(xcb_conn_aux, xcb_root_aux);
        xcb_query_pointer_reply_t *qr = xcb_query_pointer_reply(xcb_conn_aux, qc, NULL);
        if (qr) { px = qr->root_x; py = qr->root_y; free(qr); }
    }
    OutputInfo *oi = find_output_at_point(px, py);
    struct wl_output *target = oi ? oi->wl_output : NULL;
    wl_snapit.active_scale = oi ? (oi->scale > 0 ? oi->scale : 1) : 1;
    snapit.origin_x = oi ? oi->x : 0;
    snapit.origin_y = oi ? oi->y : 0;
    snapit.phys_w = oi ? oi->phys_w : 0;
    snapit.phys_h = oi ? oi->phys_h : 0;
    snapit_output_scale = wl_snapit.active_scale;

    wl_snapit.surface = wl_compositor_create_surface(compositor);
    wl_snapit.layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer_shell, wl_snapit.surface, target,
        ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY, "wfinfo-snapit");

    zwlr_layer_surface_v1_set_anchor(wl_snapit.layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
        ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_size(wl_snapit.layer_surface, 0, 0);
    zwlr_layer_surface_v1_set_exclusive_zone(wl_snapit.layer_surface, -1);
    zwlr_layer_surface_v1_set_keyboard_interactivity(wl_snapit.layer_surface,
        ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);

    zwlr_layer_surface_v1_add_listener(wl_snapit.layer_surface,
        &snapit_layer_listener, NULL);
    wl_surface_commit(wl_snapit.surface);
}

static void wl_close_snapit(void)
{
    if (wl_snapit.layer_surface) {
        zwlr_layer_surface_v1_destroy(wl_snapit.layer_surface);
        wl_snapit.layer_surface = NULL;
    }
    if (wl_snapit.surface) {
        wl_surface_destroy(wl_snapit.surface);
        wl_snapit.surface = NULL;
    }
    cleanup_framebufs(wl_snapit.bufs);
    if (snapit.hint_cs) { cairo_surface_destroy(snapit.hint_cs); snapit.hint_cs = NULL; }
}

static void wl_snapit_commit(void)
{
    if (!wl_snapit.surface || !snapit.configured || snapit.surf_w <= 0 || snapit.surf_h <= 0) return;

    WlFrameBuf *fb = NULL;
    for (int i = 0; i < 2; i++) {
        if (!wl_snapit.bufs[i].busy) { fb = &wl_snapit.bufs[i]; break; }
    }
    if (!fb) return;

    int s = wl_snapit.active_scale > 0 ? wl_snapit.active_scale : 1;
    int bw = snapit.surf_w * s, bh = snapit.surf_h * s;
    render_snapit(fb->data, snapit.surf_w, snapit.surf_h, s);

    fb->busy = 1;
    wl_surface_set_buffer_scale(wl_snapit.surface, s);
    wl_surface_attach(wl_snapit.surface, fb->wl_buf, 0, 0);
    wl_surface_damage_buffer(wl_snapit.surface, 0, 0, bw, bh);
    wl_surface_commit(wl_snapit.surface);
}

static void try_hide_real_cursor(void)
{
    if (!wl_cursor_hidden && wl_pointer && wl_last_enter_serial) {
        wl_pointer_set_cursor(wl_pointer, wl_last_enter_serial, NULL, 0, 0);
        wl_cursor_hidden = 1;
    }
}

/* ---- rw_tick: active pointer polling via X11/evdev ---- */

static void wl_rw_tick(void)
{
    if (!wl_rw.surface || !rw.visible || !rw.configured)
        return;
    if (wl_has_pointer_focus)
        return;

    int have_ptr = 0;
    int rx = 0, ry = 0;
    unsigned int mask = 0;

    if (qp_pending) {
        xcb_query_pointer_reply_t *r =
            xcb_query_pointer_reply(xcb_conn_aux, qp_cookie, NULL);
        if (r) {
            if (wl_rw.output_phys_w > 0 && wl_rw.output_logical_w > 0) {
                rx = (int)(r->root_x * (double)wl_rw.output_logical_w / wl_rw.output_phys_w);
                ry = (int)(r->root_y * (double)wl_rw.output_logical_h / wl_rw.output_phys_h);
            } else {
                int s = wl_rw.active_scale > 0 ? wl_rw.active_scale : 1;
                rx = r->root_x / s;
                ry = r->root_y / s;
            }
            mask = r->mask;
            free(r);
            have_ptr = 1;
        }
        qp_pending = 0;
    }

    if (have_ptr) {
        int over = (rx >= rw.offset_x && rx < rw.offset_x + rw.total_w &&
                    ry >= rw.offset_y && ry < rw.offset_y + RW_TOTAL_H);
        int btn_now = (mask & XCB_BUTTON_MASK_1) != 0;

        if (btn_now && !wl_btn_prev && over && !rw.dragging) {
            double cx = rx - rw.offset_x;
            double cy = ry - rw.offset_y;
            if (cx >= rw.total_w - 30 && cy >= 0 && cy <= RW_TITLE_H) {
                backend->hide_rw();
                rw.visible = 0;
                rw.configured = 0;
                rw.dragging = 0;
                wl_btn_prev = btn_now;
                goto send_query;
            }
            sw_cursor_x = rx - rw.offset_x;
            sw_cursor_y = ry - rw.offset_y;
            sw_cursor_show = 1;
            try_hide_real_cursor();
            wl_rw_commit();
            rw.dragging = 1;
            rw.drag_start_px = rx;
            rw.drag_start_py = ry;
            rw.drag_start_ox = rw.offset_x;
            rw.drag_start_oy = rw.offset_y;
            pointer_on_rw = 1;
            {
                struct wl_region *region = wl_compositor_create_region(compositor);
                wl_surface_set_input_region(wl_rw.surface, region);
                wl_region_destroy(region);
                wl_surface_commit(wl_rw.surface);
            }
        } else if (!btn_now && wl_btn_prev && rw.dragging) {
            rw.dragging = 0;
            wl_rw_set_input_region(0);
            wl_rw_commit();
        }
        wl_btn_prev = btn_now;

        if (rw.dragging) {
            int new_ox = rw.drag_start_ox + (rx - (int)rw.drag_start_px);
            int new_oy = rw.drag_start_oy + (ry - (int)rw.drag_start_py);
            if (new_ox != rw.offset_x || new_oy != rw.offset_y) {
                rw.offset_x = new_ox;
                rw.offset_y = new_oy;
                rw_ptr_x = rx;
                rw_ptr_y = ry;
                wl_rw_commit();
            }
        } else {
            pointer_on_rw = over;
            int ncx = rx - rw.offset_x;
            int ncy = ry - rw.offset_y;
            if (over && (ncx != sw_cursor_x || ncy != sw_cursor_y || !sw_cursor_show)) {
                sw_cursor_x = ncx;
                sw_cursor_y = ncy;
                sw_cursor_show = 1;
                try_hide_real_cursor();
                wl_rw_commit();
            } else if (!over && sw_cursor_show) {
                sw_cursor_show = 0;
                wl_rw_commit();
            }
        }
    }

send_query:
    qp_cookie = xcb_query_pointer(xcb_conn_aux, xcb_root_aux);
    qp_pending = 1;
    xcb_flush(xcb_conn_aux);
}

/* ---- backend constructor ---- */

static OverlayBackend wl_backend = {
    .init             = wl_init,
    .destroy          = wl_destroy,
    .get_fd           = wl_get_fd,
    .dispatch         = wl_dispatch,
    .flush            = wl_flush,
    .show_panel       = wl_show_panel,
    .hide_panel       = wl_hide_panel,
    .rerender_panel   = wl_rerender_panel,
    .show_rw          = wl_show_rw,
    .hide_rw          = wl_hide_rw,
    .rw_redraw        = wl_rw_redraw,
    .rw_set_input_region = wl_rw_set_input_region,
    .start_snapit     = wl_start_snapit,
    .close_snapit     = wl_close_snapit,
    .snapit_redraw    = wl_snapit_commit,
    .rw_tick          = wl_rw_tick,
};

OverlayBackend *wayland_backend_create(void)
{
    return &wl_backend;
}
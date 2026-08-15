#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include "overlay-plugin.hpp"
#include "overlay.hpp"
#include "vklayer-composite.hpp"

extern DeviceData *g_active_dd;

static void stub_show_rw(void) {
    if (!rw.visible && g_active_dd) {
        int sw = static_cast<int>(g_active_dd->sc.width);
        int sh = static_cast<int>(g_active_dd->sc.height);
        if (sw > 0 && sh > 0) {
            rw.offset_x = (sw - rw.total_w) / 2;
            rw.offset_y = (sh - RW_TOTAL_H) / 2;
            if (rw.offset_x < 0) rw.offset_x = 0;
            if (rw.offset_y < 0) rw.offset_y = 0;
        }
        rw.dragging = 0;
    }
    rw.visible = 1;
    composite_mark_rw_dirty();
}
static void stub_hide_rw(void) { rw.visible = 0; rw.configured = 0; }
static void stub_rw_redraw(void) {}
static void stub_rw_set_input_region(int fs) { (void)fs; }
static void stub_show_panel(int id) { ensure_panel_capacity(id); panels[id].visible = 1; panels[id].configured = 1; }
static void stub_hide_panel(int id) { if (id >= 0 && static_cast<size_t>(id) < panels.size()) { panels[id].visible = 0; panels[id].configured = 0; } }
static void stub_rerender_panel(int id) { (void)id; }
static void stub_start_snapit(int w, int h) {
    (void)w; (void)h;
    snapit.active = 1;
    snapit_cache_hint();
    extern int snap_btn_prev;
    snap_btn_prev = -1;
    extern int snapit_tint_ready;
    extern int snapit_cursor_ready;
    snapit_tint_ready = 0;
    snapit_cursor_ready = 0;
}
static void stub_close_snapit(void) { snapit.active = 0; }
static void stub_snapit_redraw(void) {}
static int  stub_init(void) { return 0; }
static void stub_destroy(void) {}
static int  stub_get_fd(void) { return -1; }
static int  stub_dispatch(void) { return 0; }
static void stub_flush(void) {}
static void stub_rw_tick(void) {}

static OverlayBackend stub_backend = {
    stub_init,
    stub_destroy,
    stub_get_fd,
    stub_dispatch,
    stub_flush,
    stub_show_panel,
    stub_hide_panel,
    stub_rerender_panel,
    stub_show_rw,
    stub_hide_rw,
    stub_rw_redraw,
    stub_rw_set_input_region,
    stub_start_snapit,
    stub_close_snapit,
    stub_snapit_redraw,
    stub_rw_tick,
};

static void plugin_on_warframe_instance(void)
{
    backend = &stub_backend;
    load_icons();
}

static void plugin_log_visible_panels(void)
{
    for (size_t pi = 0; pi < panels.size(); pi++) {
        if (panels[pi].visible)
            layer_log("  panel[%zu] vis=1 pos=(%d,%d) %dx%d snapit=%d",
                      pi, panels[pi].x, panels[pi].y,
                      panels[pi].w, panels[pi].h, panels[pi].snapit);
    }
}

static OverlayPlugin g_plugin = {
    plugin_on_warframe_instance,
    process_line,
    plugin_log_visible_panels,
    composite_init_pipeline,
    composite_cleanup,
    composite_record_overlays,
};

extern "C" OverlayPlugin *wfinfo_overlay_get(void)
{
    return &g_plugin;
}

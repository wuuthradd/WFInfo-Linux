#ifndef OVERLAY_H
#define OVERLAY_H

#include <time.h>
#include <cairo/cairo.h>

#define MAX_PANELS 68
#define HIDE_DEFAULT_REWARD_MS  10000
#define HIDE_DEFAULT_SNAPIT_MS  20000

#define RW_CARD_W   250
#define RW_TITLE_H   27
#define RW_TOTAL_H  240
#define RW_MAX_PARTS  4

typedef struct {
    int x, y, w, h;
    int visible;
    int configured;
    time_t hide_at;
    char name[256];
    char plat[64];
    char ducats[64];
    char owned[64];
    char detected[64];
    char volume[128];
    char set_plat[64];
    char highlight[32];
    int vaulted;
    int mastered;
    int warning;
    int snapit;
    int hide_info;
    int high_contrast;
    double min_eff;
    double max_eff;
    int hide_delay;
} Panel;

typedef struct {
    char name[256];
    char plat[64];
    char ducats[64];
    char owned[64];
    char volume[128];
    char set_plat[64];
    char highlight[32];
    int vaulted;
    int mastered;
    int hide_info;
    int filled;
} RwPart;

typedef struct {
    int visible;
    int configured;
    int count;
    int total_w;
    int surf_w, surf_h;
    int offset_x, offset_y;
    int dragging;
    double drag_start_px, drag_start_py;
    int drag_start_ox, drag_start_oy;
    RwPart parts[RW_MAX_PARTS];
} RwState;

typedef struct {
    int active;
    int configured;
    int dragging;
    double start_x, start_y;
    double cur_x, cur_y;
    double dash_offset;
    int surf_w, surf_h;
    int phys_w, phys_h;
    int origin_x, origin_y;
    cairo_surface_t *hint_cs;
    int hint_w, hint_h;
} SnapItState;

typedef struct {
    int  (*init)(void);
    void (*destroy)(void);
    int  (*get_fd)(void);
    int  (*dispatch)(void);
    void (*flush)(void);
    void (*show_panel)(int id);
    void (*hide_panel)(int id);
    void (*rerender_panel)(int id);
    void (*show_rw)(void);
    void (*hide_rw)(void);
    void (*rw_redraw)(void);
    void (*rw_set_input_region)(int fullscreen);
    void (*start_snapit)(int w, int h);
    void (*close_snapit)(void);
    void (*snapit_redraw)(void);
    void (*rw_tick)(void);
} OverlayBackend;

extern Panel panels[MAX_PANELS];
extern RwState rw;
extern SnapItState snapit;
extern int running;
extern int pointer_on_rw;
extern double rw_ptr_x, rw_ptr_y;
extern OverlayBackend *backend;
extern int snapit_output_scale;

extern cairo_surface_t *plat_icon_surface;
extern cairo_surface_t *ducat_icon_surface;
extern cairo_surface_t *warning_icon_surface;
extern cairo_surface_t *wflogo_icon_surface;
extern cairo_surface_t *bg_surface;

void load_icons(void);
void draw_icon_surface(cairo_t *cr, cairo_surface_t *icon,
                       double x, double y, double target_w, double target_h);
void render_panel(Panel *p, void *buf_data, int scale);
void render_rw_content(cairo_t *cr);
void render_snapit(void *buf_data, int w, int h, int scale);
void snapit_cache_hint(void);
void hide_panel_by_id(int id);
void process_line(const char *line);

typedef struct { int x, y, w, h; } MonitorRect;
void get_monitor_at_point(void *display, int px, int py, MonitorRect *out);

void handle_rw_button_press(double px, double py);
void handle_rw_button_release(void);
void handle_rw_motion(double px, double py);
void handle_snapit_press(double x, double y);
void handle_snapit_release(double x, double y);
void handle_snapit_motion(double x, double y);

const char *json_find_key(const char *json, const char *key);
int json_get_int(const char *json, const char *key, int def);
int json_get_bool(const char *json, const char *key, int def);
void json_get_string(const char *json, const char *key, char *out, int maxlen);
double json_get_double(const char *json, const char *key, double def);

OverlayBackend *wayland_backend_create(void);
OverlayBackend *x11_backend_create(void);

#endif
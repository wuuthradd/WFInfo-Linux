#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <pango/pangocairo.h>
#include "overlay.hpp"
#include "icon_data.hpp"
#include "bg_data.hpp"

std::vector<Panel> panels;
RwState rw;
SnapItState snapit;
int running = 1;
int pointer_on_rw;
double rw_ptr_x, rw_ptr_y;
OverlayBackend *backend;
cairo_surface_t *plat_icon_surface;
cairo_surface_t *ducat_icon_surface;
cairo_surface_t *warning_icon_surface;
cairo_surface_t *wflogo_icon_surface;
cairo_surface_t *bg_surface;

static PangoFontDescription *cached_panel_font;
static PangoFontDescription *cached_rw_bold12;
static PangoFontDescription *cached_rw_11;
static PangoFontDescription *cached_rw_9;
static PangoFontDescription *cached_rw_15;
static PangoFontDescription *cached_rw_bold16;
static PangoFontDescription *cached_snapit_hint;

static void ensure_font_cache(void)
{
    if (cached_panel_font) return;
    cached_panel_font = pango_font_description_from_string("Roboto Condensed 12");
    cached_rw_bold12 = pango_font_description_from_string("Roboto Condensed Bold 12");
    cached_rw_11 = pango_font_description_from_string("Roboto Condensed 11");
    cached_rw_9 = pango_font_description_from_string("Roboto Condensed 9");
    cached_rw_15 = pango_font_description_from_string("Roboto Condensed 15");
    cached_rw_bold16 = pango_font_description_from_string("Roboto Condensed Bold 16");
    cached_snapit_hint = pango_font_description_from_string("Sans 48");
}

static void set_panel_font(double size, int bold)
{
    pango_font_description_set_weight(cached_panel_font, bold ? PANGO_WEIGHT_BOLD : PANGO_WEIGHT_NORMAL);
    pango_font_description_set_absolute_size(cached_panel_font, size * PANGO_SCALE);
}

/* ---- embedded icon loading ---- */

typedef struct { const unsigned char *data; unsigned int offset; unsigned int size; } PngReadCtx;

static cairo_status_t png_read_func(void *closure, unsigned char *buf, unsigned int length)
{
    auto *ctx = static_cast<PngReadCtx*>(closure);
    if (ctx->offset + length > ctx->size) return CAIRO_STATUS_READ_ERROR;
    memcpy(buf, ctx->data + ctx->offset, length);
    ctx->offset += length;
    return CAIRO_STATUS_SUCCESS;
}

static cairo_surface_t *load_embedded_png(const unsigned char *data, unsigned int size)
{
    PngReadCtx ctx = { data, 0, size };
    return cairo_image_surface_create_from_png_stream(png_read_func, &ctx);
}

void load_icons(void)
{
    plat_icon_surface    = load_embedded_png(plat_icon_data,    plat_icon_size);
    ducat_icon_surface   = load_embedded_png(ducat_icon_data,   ducat_icon_size);
    warning_icon_surface = load_embedded_png(warning_icon_data, warning_icon_size);
    wflogo_icon_surface  = load_embedded_png(wflogo_icon_data,  wflogo_icon_size);
    bg_surface           = load_embedded_png(bg_png_data,       bg_png_size);
}

/* ---- minimal JSON helpers ---- */

const char *json_find_key(const char *json, const char *key)
{
    char pattern[128];
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    const char *p = json;
    while ((p = strstr(p, pattern)) != NULL) {
        const char *after = p + strlen(pattern);
        while (*after == ' ') after++;
        if (*after == ':') {
            after++;
            while (*after == ' ') after++;
            return after;
        }
        p = after;
    }
    return NULL;
}

int json_get_int(const char *json, const char *key, int def)
{
    const char *v = json_find_key(json, key);
    if (!v) return def;
    return atoi(v);
}

int json_get_bool(const char *json, const char *key, int def)
{
    const char *v = json_find_key(json, key);
    if (!v) return def;
    if (strncmp(v, "true", 4) == 0) return 1;
    if (strncmp(v, "false", 5) == 0) return 0;
    return def;
}

void json_get_string(const char *json, const char *key, char *out, int maxlen)
{
    out[0] = '\0';
    const char *v = json_find_key(json, key);
    if (!v || *v != '"') return;
    v++;
    int i = 0;
    while (*v && *v != '"' && i < maxlen - 1) {
        if (*v == '\\' && *(v + 1)) {
            v++;
            switch (*v) {
                case 'n':  out[i++] = '\n'; break;
                case 'r':  out[i++] = '\r'; break;
                case 't':  out[i++] = '\t'; break;
                case '"':  out[i++] = '"';  break;
                case '\\': out[i++] = '\\'; break;
                default:   out[i++] = *v;   break;
            }
        } else {
            out[i++] = *v;
        }
        v++;
    }
    out[i] = '\0';
}

double json_get_double(const char *json, const char *key, double def)
{
    const char *v = json_find_key(json, key);
    if (!v) return def;
    return atof(v);
}


/* ---- rendering ---- */

void draw_icon_surface(cairo_t *cr, cairo_surface_t *icon,
                       double x, double y, double target_w, double target_h)
{
    if (!icon || cairo_surface_status(icon) != CAIRO_STATUS_SUCCESS) return;
    int iw = cairo_image_surface_get_width(icon);
    int ih = cairo_image_surface_get_height(icon);
    if (iw <= 0 || ih <= 0) return;
    double sx = target_w / iw;
    double sy = target_h / ih;
    cairo_save(cr);
    cairo_translate(cr, x, y);
    cairo_scale(cr, sx, sy);
    cairo_set_source_surface(cr, icon, 0, 0);
    cairo_paint(cr);
    cairo_restore(cr);
}

void render_panel(Panel *p, void *buf_data, int scale)
{
    int bw = p->w * scale, bh = p->h * scale;
    cairo_surface_t *cs = cairo_image_surface_create_for_data(
        static_cast<unsigned char*>(buf_data), CAIRO_FORMAT_ARGB32, bw, bh, bw * 4);
    if (scale > 1)
        cairo_surface_set_device_scale(cs, scale, scale);
    cairo_t *cr = cairo_create(cs);

    cairo_surface_t *bg = bg_surface;
    if (p->high_contrast) {
        cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
        cairo_set_source_rgba(cr, 0, 0, 0, 1.0);
        cairo_paint(cr);
        cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
    } else if (bg && cairo_surface_status(bg) == CAIRO_STATUS_SUCCESS) {
        int bg_w = cairo_image_surface_get_width(bg);
        int bg_h = cairo_image_surface_get_height(bg);
        cairo_save(cr);
        cairo_scale(cr, (double)p->w / bg_w, (double)p->h / bg_h);
        cairo_set_source_surface(cr, bg, 0, 0);
        cairo_paint(cr);
        cairo_restore(cr);
    }

    if (p->snapit) {
        cairo_set_source_rgba(cr, 0, 0, 0, 0.35);
        cairo_paint(cr);
    }

    /* Default text color: WFInfo blue-gray (#b1d0d9) */
    double name_r = 0.694, name_g = 0.816, name_b = 0.851;
    double plat_r = 0.694, plat_g = 0.816, plat_b = 0.851;
    double ducat_r = 0.694, ducat_g = 0.816, ducat_b = 0.851;
    double owned_r = 0.694, owned_g = 0.816, owned_b = 0.851;
    int name_bold = 0, plat_bold = 0, ducat_bold = 0, owned_bold = 0;

    if (strcmp(p->highlight, "plat") == 0) {
        plat_r = 0; plat_g = 1.0; plat_b = 0;
        name_r = 0; name_g = 1.0; name_b = 0;
        plat_bold = 1; name_bold = 1;
    } else if (strcmp(p->highlight, "ducat") == 0) {
        ducat_r = 1.0; ducat_g = 0.843; ducat_b = 0;
        name_r = 1.0;  name_g = 0.843;  name_b = 0;
        ducat_bold = 1; name_bold = 1;
    } else if (strcmp(p->highlight, "owned") == 0) {
        owned_r = 0; owned_g = 1.0; owned_b = 0.843;
        name_r = 0;  name_g = 1.0;  name_b = 0.843;
        owned_bold = 1; name_bold = 1;
    }

    /* Adaptive layout: padding, row gap, and font sizes scale with panel height */
    double pad = p->h * 0.035;
    if (pad < 2) pad = 2;
    double row_gap = p->h * 0.06;
    if (row_gap < 2) row_gap = 2;
    int nrows = 5;
    double avail_h = p->h - 2 * pad - (nrows - 1) * row_gap;
    double rh = avail_h / nrows;
    if (rh < 8) rh = 8;

    double fs = rh * 0.75;
    if (fs < 6) fs = 6;
    if (fs > 18) fs = 18;
    double fs_small = fs * 0.85;
    if (fs_small < 5) fs_small = 5;
    double fs_owned = fs * 0.85;
    if (fs_owned < 5) fs_owned = 5;

    double icon_sz = fs * 1.5;
    if (icon_sz < 8) icon_sz = 8;

    double row_y[5];
    for (int i = 0; i < 5; i++)
        row_y[i] = pad + i * (rh + row_gap);

    ensure_font_cache();
    PangoLayout *layout = pango_cairo_create_layout(cr);
    int tw, th;

    if (!p->hide_info && p->owned[0]) {
        char owned_buf[192];
        if (p->mastered)
            snprintf(owned_buf, sizeof(owned_buf), "\xe2\x9c\x93 %s OWNED", p->owned);
        else
            snprintf(owned_buf, sizeof(owned_buf), "%s OWNED", p->owned);
        if (p->detected[0]) {
            char det_buf[80];
            snprintf(det_buf, sizeof(det_buf), " (%s FOUND)", p->detected);
            strncat(owned_buf, det_buf, sizeof(owned_buf) - strlen(owned_buf) - 1);
        }
        set_panel_font(fs_owned, owned_bold);
        pango_layout_set_font_description(layout, cached_panel_font);
        pango_layout_set_text(layout, owned_buf, -1);
        pango_layout_set_width(layout, (int)((p->w * 0.80 - pad) * PANGO_SCALE));
        pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_END);
        pango_layout_get_pixel_size(layout, NULL, &th);
        cairo_set_source_rgba(cr, owned_r, owned_g, owned_b, 1.0);
        cairo_move_to(cr, pad, row_y[0] + (rh - th) / 2);
        pango_cairo_show_layout(cr, layout);
    }

    if (!p->hide_info && p->vaulted) {
        pango_layout_set_width(layout, -1);
        pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_NONE);
        set_panel_font(fs_owned, 0);
        pango_layout_set_font_description(layout, cached_panel_font);
        pango_layout_set_text(layout, "VAULTED", -1);
        pango_layout_get_pixel_size(layout, &tw, &th);
        cairo_set_source_rgba(cr, 0.510, 0.549, 0.588, 1.0);
        cairo_move_to(cr, p->w - tw - pad, row_y[0] + (rh - th) / 2);
        pango_cairo_show_layout(cr, layout);
    }

    {
        set_panel_font(fs, name_bold);
        pango_layout_set_font_description(layout, cached_panel_font);
        pango_layout_set_text(layout, p->name, -1);
        pango_layout_set_width(layout, (int)((p->w - 2 * pad) * PANGO_SCALE));
        pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_END);
        pango_layout_set_alignment(layout, PANGO_ALIGN_CENTER);
        pango_layout_get_pixel_size(layout, NULL, &th);
        cairo_set_source_rgba(cr, name_r, name_g, name_b, 1.0);
        cairo_move_to(cr, pad, row_y[1] + (rh - th) / 2);
        pango_cairo_show_layout(cr, layout);
        pango_layout_set_alignment(layout, PANGO_ALIGN_LEFT);
        pango_layout_set_width(layout, -1);
        pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_NONE);
    }

    if (!p->hide_info) {

    {
        double icy = row_y[2] + (rh - icon_sz) / 2;
        double text_y = row_y[2];

        int ngroups = 2;
        char eff_buf[32] = "";
        if (p->snapit) {
            double plat_val = atof(p->plat);
            double duc_val  = atof(p->ducats);
            if (plat_val > 0) {
                snprintf(eff_buf, sizeof(eff_buf), "%.1f", duc_val / plat_val);
                ngroups = 3;
            }
        }

        double slot_w = (p->w - 2 * pad) / ngroups;

        set_panel_font(fs, plat_bold);
        pango_layout_set_font_description(layout, cached_panel_font);

        {
            pango_layout_set_text(layout, p->plat, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double grp_w = tw + 2 + icon_sz;
            double gx = pad + slot_w * 0 + (slot_w - grp_w) / 2;
            cairo_set_source_rgba(cr, plat_r, plat_g, plat_b, 1.0);
            cairo_move_to(cr, gx, text_y + (rh - th) / 2);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, plat_icon_surface, gx + tw + 2, icy, icon_sz, icon_sz);
        }

        set_panel_font(fs, ducat_bold);
        pango_layout_set_font_description(layout, cached_panel_font);
        {
            pango_layout_set_text(layout, p->ducats, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double grp_w = tw + 2 + icon_sz;
            double gx = pad + slot_w * 1 + (slot_w - grp_w) / 2;
            cairo_set_source_rgba(cr, ducat_r, ducat_g, ducat_b, 1.0);
            cairo_move_to(cr, gx, text_y + (rh - th) / 2);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, ducat_icon_surface, gx + tw + 2, icy, icon_sz, icon_sz);
        }

        if (ngroups == 3 && eff_buf[0]) {
            set_panel_font(fs, 0);
            pango_layout_set_font_description(layout, cached_panel_font);
            pango_layout_set_text(layout, eff_buf, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double esz = icon_sz * 0.65;
            double grp_w = tw + 2 + esz * 1.4;
            double gx = pad + slot_w * 2 + (slot_w - grp_w) / 2;
            double eff_ratio = atof(p->ducats) / atof(p->plat);
            if (eff_ratio > p->max_eff)
                cairo_set_source_rgba(cr, 0.486, 0.988, 0, 1.0);
            else if (eff_ratio < p->min_eff)
                cairo_set_source_rgba(cr, 0.545, 0, 0, 1.0);
            else
                cairo_set_source_rgba(cr, 0.682, 0.780, 0.808, 0.7);
            cairo_move_to(cr, gx, text_y + (rh - th) / 2);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, ducat_icon_surface, gx + tw + 2, icy, esz, esz);
            draw_icon_surface(cr, plat_icon_surface, gx + tw + 2 + esz * 0.4, icy + esz * 0.4, esz, esz);
        }
    }

    if (p->volume[0]) {
        char vol_buf[160];
        snprintf(vol_buf, sizeof(vol_buf), "%s sold last 48hrs", p->volume);
        set_panel_font(fs_small, 0);
        pango_layout_set_font_description(layout, cached_panel_font);
        pango_layout_set_text(layout, vol_buf, -1);
        pango_layout_get_pixel_size(layout, &tw, &th);
        double vol_x = (p->w - tw) / 2;
        if (vol_x < pad) vol_x = pad;
        cairo_set_source_rgba(cr, 0.694, 0.816, 0.851, 1.0);
        cairo_move_to(cr, vol_x, row_y[3] + (rh - th) / 2);
        pango_cairo_show_layout(cr, layout);
    }

    if (p->set_plat[0]) {
        char set_buf[128];
        snprintf(set_buf, sizeof(set_buf), "Full set price: %s", p->set_plat);
        set_panel_font(fs_small, 0);
        pango_layout_set_font_description(layout, cached_panel_font);
        pango_layout_set_text(layout, set_buf, -1);
        pango_layout_get_pixel_size(layout, &tw, &th);
        double grp_w_set = tw + 2 + icon_sz;
        double set_x = (p->w - grp_w_set) / 2;
        if (set_x < pad) set_x = pad;
        cairo_set_source_rgba(cr, 0.694, 0.816, 0.851, 1.0);
        cairo_move_to(cr, set_x, row_y[4] + (rh - th) / 2);
        pango_cairo_show_layout(cr, layout);
        draw_icon_surface(cr, plat_icon_surface, set_x + tw + 2, row_y[4] + (rh - icon_sz) / 2, icon_sz, icon_sz);
    }

    }

    if (p->warning) {
        double warn_sz = rh * 0.9;
        if (warn_sz < 10) warn_sz = 10;
        draw_icon_surface(cr, warning_icon_surface,
            pad, p->h - warn_sz - pad, warn_sz, warn_sz);
    }

    g_object_unref(layout);
    cairo_destroy(cr);
    cairo_surface_destroy(cs);
}

void render_rw_content(cairo_t *cr)
{
    RwState *r = &rw;
    int w = r->total_w;

    cairo_set_source_rgba(cr, 0.059, 0.059, 0.059, 1.0);
    cairo_rectangle(cr, 0, 0, w, RW_TITLE_H);
    cairo_fill(cr);

    ensure_font_cache();
    PangoLayout *layout = pango_cairo_create_layout(cr);
    int tw, th;

    draw_icon_surface(cr, wflogo_icon_surface, 2, 1, 24, 24);

    pango_layout_set_font_description(layout, cached_rw_bold12);
    pango_layout_set_text(layout, "Rewards", -1);
    pango_layout_get_pixel_size(layout, &tw, &th);
    cairo_set_source_rgba(cr, 1, 1, 1, 1);
    cairo_move_to(cr, 28, (RW_TITLE_H - th) / 2);
    pango_cairo_show_layout(cr, layout);

    pango_layout_set_font_description(layout, cached_rw_11);
    pango_layout_set_text(layout, "x", -1);
    pango_layout_get_pixel_size(layout, &tw, &th);
    cairo_move_to(cr, w - 15 - tw / 2, (RW_TITLE_H - th) / 2);
    pango_cairo_show_layout(cr, layout);

    int content_y = RW_TITLE_H;
    int content_h = RW_TOTAL_H - RW_TITLE_H;

    for (int i = 0; i < r->count && i < RW_MAX_PARTS; i++) {
        int cx = i * RW_CARD_W;
        int cw = RW_CARD_W;

        cairo_set_source_rgba(cr, 0.392, 0.392, 0.392, 1.0);
        cairo_set_line_width(cr, 1.0);
        cairo_rectangle(cr, cx + 0.5, content_y + 0.5, cw - 1.0, content_h - 1.0);
        cairo_stroke(cr);

        RwPart *part = &r->parts[i];
        if (!part->filled) continue;

        double name_r = 0.694, name_g = 0.816, name_b = 0.851;
        double plat_r = 0.694, plat_g = 0.816, plat_b = 0.851;
        double ducat_r = 0.694, ducat_g = 0.816, ducat_b = 0.851;
        double owned_r = 0.694, owned_g = 0.816, owned_b = 0.851;
        int name_bold = 0;

        if (strcmp(part->highlight, "plat") == 0) {
            plat_r = 0; plat_g = 1.0; plat_b = 0;
            name_r = 0; name_g = 1.0; name_b = 0;
            name_bold = 1;
        } else if (strcmp(part->highlight, "ducat") == 0) {
            ducat_r = 1.0; ducat_g = 0.843; ducat_b = 0;
            name_r = 1.0; name_g = 0.843; name_b = 0;
            name_bold = 1;
        } else if (strcmp(part->highlight, "owned") == 0) {
            owned_r = 0; owned_g = 1.0; owned_b = 0.843;
            name_r = 0;  name_g = 1.0;  name_b = 0.843;
            name_bold = 1;
        }

        double pad_x = 10;
        double inner_w = cw - 2 * pad_x;
        double row_owned_h = 25;
        double row_name_h = 65;
        double row_plat_h = 27;
        double row_vol_h = 25;
        double row_set_h = 27;
        double gap = 3;
        double total_rows_h = row_owned_h + row_name_h + row_plat_h + row_vol_h + row_set_h + gap * 4;
        double start_y = content_y + (content_h - total_rows_h) / 2.0;
        double ry = start_y;

        if (!part->hide_info) {
            if (part->owned[0]) {
                char owned_buf[192];
                if (part->mastered)
                    snprintf(owned_buf, sizeof(owned_buf), "\xe2\x9c\x93 %s OWNED", part->owned);
                else
                    snprintf(owned_buf, sizeof(owned_buf), "%s OWNED", part->owned);
                pango_layout_set_font_description(layout, cached_rw_9);
                pango_layout_set_text(layout, owned_buf, -1);
                pango_layout_set_width(layout, (int)(inner_w * 0.80 * PANGO_SCALE));
                pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_END);
                pango_layout_get_pixel_size(layout, NULL, &th);
                cairo_set_source_rgba(cr, owned_r, owned_g, owned_b, 1.0);
                cairo_move_to(cr, cx + pad_x, ry + (row_owned_h - th) / 2.0);
                pango_cairo_show_layout(cr, layout);
                pango_layout_set_width(layout, -1);
                pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_NONE);
            }
            if (part->vaulted) {
                pango_layout_set_font_description(layout, cached_rw_9);
                pango_layout_set_text(layout, "VAULTED", -1);
                pango_layout_get_pixel_size(layout, &tw, &th);
                cairo_set_source_rgba(cr, 0.510, 0.549, 0.588, 1.0);
                cairo_move_to(cr, cx + cw - pad_x - tw, ry + (row_owned_h - th) / 2.0);
                pango_cairo_show_layout(cr, layout);
            }
        }
        ry += row_owned_h + gap;

        {
            pango_layout_set_font_description(layout, name_bold ? cached_rw_bold16 : cached_rw_15);
            pango_layout_set_text(layout, part->name, -1);
            pango_layout_set_width(layout, (int)(inner_w * PANGO_SCALE));
            pango_layout_set_wrap(layout, PANGO_WRAP_WORD_CHAR);
            pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_END);
            pango_layout_set_height(layout, (int)(row_name_h * PANGO_SCALE));
            pango_layout_set_alignment(layout, PANGO_ALIGN_CENTER);
            pango_layout_get_pixel_size(layout, NULL, &th);
            cairo_set_source_rgba(cr, name_r, name_g, name_b, 1.0);
            cairo_move_to(cr, cx + pad_x, ry + (row_name_h - th) / 2.0);
            pango_cairo_show_layout(cr, layout);
            pango_layout_set_width(layout, -1);
            pango_layout_set_wrap(layout, PANGO_WRAP_WORD);
            pango_layout_set_ellipsize(layout, PANGO_ELLIPSIZE_NONE);
            pango_layout_set_alignment(layout, PANGO_ALIGN_LEFT);
            pango_layout_set_height(layout, -1);
        }
        ry += row_name_h + gap;

        if (!part->hide_info) {
            double plat_icon_sz = 22;
            double ducat_icon_w = 20;
            double ducat_icon_h = 22;
            double half_w = inner_w / 2;

            pango_layout_set_font_description(layout, cached_rw_15);
            pango_layout_set_text(layout, part->plat, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double grp_w = tw + 4 + plat_icon_sz;
            double gx = cx + pad_x + (half_w - grp_w) / 2;
            cairo_set_source_rgba(cr, plat_r, plat_g, plat_b, 1.0);
            cairo_move_to(cr, gx, ry + (row_plat_h - th) / 2.0);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, plat_icon_surface, gx + tw + 4,
                ry + (row_plat_h - plat_icon_sz) / 2.0, plat_icon_sz, plat_icon_sz);

            pango_layout_set_font_description(layout, cached_rw_15);
            pango_layout_set_text(layout, part->ducats, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            grp_w = tw + 4 + ducat_icon_w;
            gx = cx + pad_x + half_w + (half_w - grp_w) / 2;
            cairo_set_source_rgba(cr, ducat_r, ducat_g, ducat_b, 1.0);
            cairo_move_to(cr, gx, ry + (row_plat_h - th) / 2.0);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, ducat_icon_surface, gx + tw + 4,
                ry + (row_plat_h - ducat_icon_h) / 2.0, ducat_icon_w, ducat_icon_h);
        }
        ry += row_plat_h + gap;

        if (!part->hide_info && part->volume[0]) {
            char vol_buf[160];
            snprintf(vol_buf, sizeof(vol_buf), "%s sold last 48hrs", part->volume);
            pango_layout_set_font_description(layout, cached_rw_11);
            pango_layout_set_text(layout, vol_buf, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double vol_x = cx + pad_x + (inner_w - tw) / 2;
            cairo_set_source_rgba(cr, 0.604, 0.682, 0.722, 1.0);
            cairo_move_to(cr, vol_x, ry + (row_vol_h - th) / 2.0);
            pango_cairo_show_layout(cr, layout);
        }
        ry += row_vol_h + gap;

        if (!part->hide_info && part->set_plat[0]) {
            char set_buf[128];
            snprintf(set_buf, sizeof(set_buf), "Full set price: %s", part->set_plat);
            double set_icon_sz = 16;
            pango_layout_set_font_description(layout, cached_rw_11);
            pango_layout_set_text(layout, set_buf, -1);
            pango_layout_get_pixel_size(layout, &tw, &th);
            double grp_w_s = tw + 3 + set_icon_sz;
            double set_x = cx + pad_x + (inner_w - grp_w_s) / 2;
            cairo_set_source_rgba(cr, 0.604, 0.682, 0.722, 1.0);
            cairo_move_to(cr, set_x, ry + (row_set_h - th) / 2.0);
            pango_cairo_show_layout(cr, layout);
            draw_icon_surface(cr, plat_icon_surface, set_x + tw + 3,
                ry + (row_set_h - set_icon_sz) / 2.0, set_icon_sz, set_icon_sz);
        }
    }

    g_object_unref(layout);
}

void snapit_cache_hint(void)
{
    SnapItState *s = &snapit;
    if (s->hint_cs) cairo_surface_destroy(s->hint_cs);
    s->hint_cs = NULL;

    ensure_font_cache();

    /* Measure on a 1x1 scratch surface, then render on the real one.
     * Reuse the same PangoLayout for both passes. */
    cairo_surface_t *tmp = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, 1, 1);
    cairo_t *cr = cairo_create(tmp);
    PangoLayout *layout = pango_cairo_create_layout(cr);
    pango_layout_set_font_description(layout, cached_snapit_hint);
    pango_layout_set_text(layout, "Press any key to exit", -1);
    pango_layout_get_pixel_size(layout, &s->hint_w, &s->hint_h);
    cairo_destroy(cr);
    cairo_surface_destroy(tmp);

    s->hint_cs = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, s->hint_w, s->hint_h);
    cr = cairo_create(s->hint_cs);
    pango_cairo_update_layout(cr, layout);
    cairo_set_source_rgba(cr, 1.0, 1.0, 1.0, 0.7);
    pango_cairo_show_layout(cr, layout);
    g_object_unref(layout);
    cairo_destroy(cr);
}

/* ---- input handlers ---- */

void handle_rw_button_press(double px, double py)
{
    double cx = px - rw.offset_x;
    double cy = py - rw.offset_y;
    if (cx >= rw.total_w - 30 && cy >= 0 && cy <= RW_TITLE_H) {
        backend->hide_rw();
        rw.visible = 0;
        rw.configured = 0;
        rw.dragging = 0;
    } else {
        rw.dragging = 1;
        rw.drag_start_px = px;
        rw.drag_start_py = py;
        rw.drag_start_ox = rw.offset_x;
        rw.drag_start_oy = rw.offset_y;
        backend->rw_set_input_region(1);
    }
}

void handle_rw_button_release(void)
{
    if (!rw.dragging) return;
    rw.dragging = 0;
    backend->rw_set_input_region(0);
    backend->rw_redraw();
}

void handle_rw_motion(double px, double py)
{
    rw_ptr_x = px;
    rw_ptr_y = py;
    if (rw.dragging && rw.configured) {
        rw.offset_x = rw.drag_start_ox + (int)(px - rw.drag_start_px);
        rw.offset_y = rw.drag_start_oy + (int)(py - rw.drag_start_py);
        backend->rw_redraw();
    }
}

/* Route event to IPC socket (in-layer) or stdout (standalone) */
static void send_overlay_event(const char *json)
{
    if (overlay_send_event)
        overlay_send_event(json);
    else {
        fputs(json, stdout);
        fflush(stdout);
    }
}

static void snapit_teardown(void)
{
    backend->close_snapit();
    snapit.active = 0;
    snapit.configured = 0;
    snapit.dragging = 0;
}

void handle_snapit_press(double x, double y)
{
    snapit.start_x = x;
    snapit.start_y = y;
    snapit.cur_x = x;
    snapit.cur_y = y;
    snapit.dragging = 1;
}

void handle_snapit_release(double x, double y)
{
    if (!snapit.dragging) return;
    snapit.dragging = 0;

    double rx = snapit.start_x < x ? snapit.start_x : x;
    double ry = snapit.start_y < y ? snapit.start_y : y;
    double rw_val = x - snapit.start_x;
    double rh_val = y - snapit.start_y;
    if (rw_val < 0) rw_val = -rw_val;
    if (rh_val < 0) rh_val = -rh_val;

    if (rw_val >= 10 && rh_val >= 10) {
        double sx = (snapit.surf_w > 0 && snapit.phys_w > 0)
            ? (double)snapit.phys_w / snapit.surf_w : 1.0;
        double sy = (snapit.surf_h > 0 && snapit.phys_h > 0)
            ? (double)snapit.phys_h / snapit.surf_h : 1.0;
        char evt[256];
        snprintf(evt, sizeof(evt),
            "{\"event\":\"snapit_result\",\"x\":%d,\"y\":%d,\"w\":%d,\"h\":%d,\"sw\":%d,\"sh\":%d}\n",
            (int)(rx * sx + snapit.origin_x * sx),
            (int)(ry * sy + snapit.origin_y * sy),
            (int)(rw_val * sx), (int)(rh_val * sy),
            snapit.surf_w, snapit.surf_h);
        send_overlay_event(evt);
        snapit_teardown();
    }
}

void handle_snapit_motion(double x, double y)
{
    snapit.cur_x = x;
    snapit.cur_y = y;
}

/* ---- panel management ---- */

static void show_panel(int id, int x, int y, int w, int h,
                       const char *name, const char *plat,
                       const char *ducats, const char *owned,
                       const char *detected,
                       int vaulted, int mastered, int warning,
                       int is_snapit, const char *volume,
                       const char *set_plat, const char *highlight,
                       double min_eff, double max_eff,
                       int hide_delay, int hide_info, int high_contrast)
{
    if (id < 0) return;
    ensure_panel_capacity(id);
    Panel *p = &panels[id];

    p->x = x; p->y = y; p->w = w; p->h = h;
    p->vaulted = vaulted;
    p->mastered = mastered;
    p->warning = warning;
    p->snapit = is_snapit;
    p->hide_info = hide_info;
    p->high_contrast = high_contrast;
    snprintf(p->name,      sizeof(p->name),      "%s", name);
    snprintf(p->plat,      sizeof(p->plat),      "%s", plat);
    snprintf(p->ducats,    sizeof(p->ducats),    "%s", ducats);
    snprintf(p->owned,     sizeof(p->owned),     "%s", owned);
    snprintf(p->detected,  sizeof(p->detected),  "%s", detected);
    snprintf(p->volume,    sizeof(p->volume),    "%s", volume);
    snprintf(p->set_plat,  sizeof(p->set_plat),  "%s", set_plat);
    snprintf(p->highlight, sizeof(p->highlight), "%s", highlight);
    p->min_eff = min_eff;
    p->max_eff = max_eff;
    p->hide_delay = hide_delay;
    p->configured = 0;

    backend->show_panel(id);
    if (composite_mark_panel_dirty)
        composite_mark_panel_dirty(id);
    p->visible = 1;
    int delay_ms = p->hide_delay > 0
        ? p->hide_delay
        : (p->snapit ? HIDE_DEFAULT_SNAPIT_MS : HIDE_DEFAULT_REWARD_MS);
    p->hide_at = time(NULL) + (delay_ms + 999) / 1000;
}

void hide_panel_by_id(int id)
{
    if (id < 0 || static_cast<size_t>(id) >= panels.size()) return;
    Panel *p = &panels[id];
    if (!p->visible) return;

    backend->hide_panel(id);
    p->visible = 0;
    p->configured = 0;
    p->hide_at = 0;
}

static void hide_rw_internal(void)
{
    if (!rw.visible) return;
    backend->hide_rw();
    pointer_on_rw = 0;
    rw.visible = 0;
    rw.configured = 0;
    rw.dragging = 0;
}

static void hide_all(void)
{
    for (size_t i = 0; i < panels.size(); i++)
        hide_panel_by_id(static_cast<int>(i));
    hide_rw_internal();
}

static void show_rw_part(int idx, const char *name, const char *plat,
    const char *ducats, const char *owned, int vaulted, int mastered,
    const char *volume, const char *set_plat, int hide_info)
{
    if (idx < 0 || idx >= RW_MAX_PARTS) return;

    if (idx == 0) {
        for (int i = 0; i < RW_MAX_PARTS; i++) {
            rw.parts[i].highlight[0] = '\0';
            rw.parts[i].filled = 0;
        }
    }

    RwPart *p = &rw.parts[idx];
    snprintf(p->name, sizeof(p->name), "%s", name);
    snprintf(p->plat, sizeof(p->plat), "%s", plat);
    snprintf(p->ducats, sizeof(p->ducats), "%s", ducats);
    snprintf(p->owned, sizeof(p->owned), "%s", owned);
    snprintf(p->volume, sizeof(p->volume), "%s", volume);
    snprintf(p->set_plat, sizeof(p->set_plat), "%s", set_plat);
    p->highlight[0] = '\0';
    p->vaulted = vaulted;
    p->mastered = mastered;
    p->hide_info = hide_info;
    p->filled = 1;
    rw.count = idx + 1;
    rw.total_w = rw.count * RW_CARD_W;
}

static void highlight_rw_part(int idx, const char *type)
{
    if (idx < 0 || idx >= RW_MAX_PARTS) return;
    if (idx >= rw.count) return;

    snprintf(rw.parts[idx].highlight, sizeof(rw.parts[idx].highlight), "%s", type);

    if (rw.visible && rw.configured)
        backend->rw_redraw();
}

static void commit_rw(void)
{
    if (rw.count <= 0) return;
    rw.configured = 0;
    rw.dragging = 0;
    backend->show_rw();
    rw.visible = 1;
}

/* ---- command processing ---- */

void process_line(const char *line)
{
    char cmd[32];
    json_get_string(line, "cmd", cmd, sizeof(cmd));

    if (strcmp(cmd, "show") == 0) {
        char name[256], plat[64], ducats[64], owned[64], detected[64];
        char volume[128], set_plat[64], highlight[32];
        json_get_string(line, "name",      name,      sizeof(name));
        json_get_string(line, "plat",      plat,      sizeof(plat));
        json_get_string(line, "ducats",    ducats,    sizeof(ducats));
        json_get_string(line, "owned",     owned,     sizeof(owned));
        json_get_string(line, "detected",  detected,  sizeof(detected));
        json_get_string(line, "volume",    volume,    sizeof(volume));
        json_get_string(line, "set_plat",  set_plat,  sizeof(set_plat));
        json_get_string(line, "highlight", highlight, sizeof(highlight));
        int id = json_get_int(line, "id", 0);
        int x  = json_get_int(line, "x", 0);
        int y  = json_get_int(line, "y", 0);
        int w  = json_get_int(line, "w", 243);
        int h  = json_get_int(line, "h", 160);
        show_panel(id, x, y, w, h, name, plat, ducats, owned, detected,
            json_get_bool(line, "vaulted", 0),
            json_get_bool(line, "mastered", 0),
            json_get_bool(line, "warning", 0),
            json_get_bool(line, "snapit", 0),
            volume, set_plat, highlight,
            json_get_double(line, "min_eff", 1.0),
            json_get_double(line, "max_eff", 2.5),
            json_get_int(line, "delay", 0),
            json_get_bool(line, "hide_info", 0),
            json_get_bool(line, "high_contrast", 0));
    } else if (strcmp(cmd, "highlight") == 0) {
        int id = json_get_int(line, "id", -1);
        char hl[32];
        json_get_string(line, "type", hl, sizeof(hl));
        if (id >= 0 && static_cast<size_t>(id) < panels.size() && panels[id].visible) {
            snprintf(panels[id].highlight, sizeof(panels[id].highlight), "%s", hl);
            backend->rerender_panel(id);
        }
    } else if (strcmp(cmd, "hide") == 0) {
        hide_panel_by_id(json_get_int(line, "id", 0));
    } else if (strcmp(cmd, "hide_all") == 0) {
        hide_all();
    } else if (strcmp(cmd, "snapit") == 0) {
        snapit.configured = 0;
        snapit.dragging = 0;
        snapit.dash_offset = 0;
        snapit.start_x = snapit.start_y = 0;
        snapit.cur_x = snapit.cur_y = 0;
        backend->start_snapit(
            json_get_int(line, "w", 1920),
            json_get_int(line, "h", 1080));
        snapit.active = 1;
    } else if (strcmp(cmd, "cancel_snapit") == 0) {
        if (snapit.active) {
            send_overlay_event("{\"event\":\"snapit_cancel\"}\n");
            snapit_teardown();
        }
    } else if (strcmp(cmd, "rw_show") == 0) {
        char name[256], plat[64], ducats[64], owned[64], volume[128], set_plat[64], hl[32];
        json_get_string(line, "name", name, sizeof(name));
        json_get_string(line, "plat", plat, sizeof(plat));
        json_get_string(line, "ducats", ducats, sizeof(ducats));
        json_get_string(line, "owned", owned, sizeof(owned));
        json_get_string(line, "volume", volume, sizeof(volume));
        json_get_string(line, "set_plat", set_plat, sizeof(set_plat));
        json_get_string(line, "highlight", hl, sizeof(hl));
        int idx = json_get_int(line, "idx", 0);
        show_rw_part(idx, name, plat, ducats, owned,
            json_get_bool(line, "vaulted", 0),
            json_get_bool(line, "mastered", 0),
            volume, set_plat,
            json_get_bool(line, "hide_info", 0));
        if (hl[0] && idx >= 0 && idx < RW_MAX_PARTS)
            snprintf(rw.parts[idx].highlight, sizeof(rw.parts[idx].highlight), "%s", hl);
    } else if (strcmp(cmd, "rw_hide") == 0) {
        hide_rw_internal();
    } else if (strcmp(cmd, "rw_highlight") == 0) {
        char hl[32];
        json_get_string(line, "type", hl, sizeof(hl));
        highlight_rw_part(json_get_int(line, "idx", -1), hl);
    } else if (strcmp(cmd, "rw_done") == 0) {
        commit_rw();
    } else if (strcmp(cmd, "quit") == 0) {
        running = 0;
    }
}
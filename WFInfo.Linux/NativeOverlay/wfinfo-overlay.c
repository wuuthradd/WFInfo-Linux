#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <poll.h>
#include <time.h>
#include <errno.h>
#include <signal.h>
#include <fontconfig/fontconfig.h>
#include "overlay.h"

int main(void)
{
    signal(SIGPIPE, SIG_IGN);
    int use_x11 = 0;
    const char *wl_env = getenv("WAYLAND_DISPLAY");
    if (!wl_env || !wl_env[0])
        use_x11 = 1;

    if (use_x11)
        backend = x11_backend_create();
    else
        backend = wayland_backend_create();

    if (backend->init() != 0) {
        if (!use_x11) {
            fprintf(stderr, "wfinfo-overlay: Wayland init failed, trying X11\n");
            backend = x11_backend_create();
            if (backend->init() != 0) {
                fprintf(stderr, "wfinfo-overlay: X11 init also failed\n");
                return 1;
            }
        } else {
            fprintf(stderr, "wfinfo-overlay: X11 init failed\n");
            return 1;
        }
    }

    load_icons();

    const char *font_dir = getenv("WFINFO_FONT_DIR");
    if (font_dir && font_dir[0]) {
        FcConfigAppFontAddDir(FcConfigGetCurrent(), (const FcChar8 *)font_dir);
        fprintf(stderr, "wfinfo-overlay: registered font dir: %s\n", font_dir);
    }

    fprintf(stderr, "READY\n");
    fflush(stderr);

    int disp_fd = backend->get_fd();
    struct pollfd fds[2];
    fds[0].fd = disp_fd;
    fds[0].events = POLLIN;
    fds[1].fd = STDIN_FILENO;
    fds[1].events = POLLIN;

    char line_buf[4096];
    int line_pos = 0;

    while (running) {
        backend->flush();

        int timeout_ms = -1;
        time_t now = time(NULL);
        for (int i = 0; i < MAX_PANELS; i++) {
            if (panels[i].visible && panels[i].hide_at > 0) {
                int remain = (int)(panels[i].hide_at - now) * 1000;
                if (remain <= 0) remain = 0;
                if (timeout_ms < 0 || remain < timeout_ms)
                    timeout_ms = remain;
            }
        }

        if (snapit.active && snapit.dragging) {
            if (timeout_ms < 0 || timeout_ms > 50)
                timeout_ms = 50;
        }

        if (rw.visible && rw.configured) {
            if (timeout_ms < 0 || timeout_ms > 16)
                timeout_ms = 16;
        }

        int ret = poll(fds, 2, timeout_ms);

        now = time(NULL);
        for (int i = 0; i < MAX_PANELS; i++) {
            if (panels[i].visible && panels[i].hide_at > 0 && now >= panels[i].hide_at)
                hide_panel_by_id(i);
        }
        if (ret < 0 && errno != EINTR) break;
        if (ret < 0) continue;

        if (snapit.active && snapit.dragging && snapit.configured) {
            snapit.dash_offset += 1.0;
            if (snapit.dash_offset >= 10.0) snapit.dash_offset = 0.0;
            backend->snapit_redraw();
        }

        if (fds[0].revents & POLLIN) {
            if (backend->dispatch() < 0)
                break;
        }

        if (rw.visible && rw.configured && backend->rw_tick)
            backend->rw_tick();

        backend->flush();

        if (fds[1].revents & POLLIN) {
            char buf[1024];
            ssize_t n = read(STDIN_FILENO, buf, sizeof(buf));
            if (n <= 0) break;

            for (int i = 0; i < n; i++) {
                if (buf[i] == '\n') {
                    line_buf[line_pos] = '\0';
                    if (line_pos > 0)
                        process_line(line_buf);
                    line_pos = 0;
                } else if (line_pos < (int)sizeof(line_buf) - 1) {
                    line_buf[line_pos++] = buf[i];
                }
            }
        }

        if (fds[1].revents & (POLLHUP | POLLERR))
            break;
    }

    backend->destroy();
    return 0;
}

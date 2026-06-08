CC      ?= gcc

WL_PKGS := wayland-client wayland-cursor
X11_PKGS := x11 x11-xcb xcb xfixes xrender xext xi xrandr
COMMON_PKGS := cairo pangocairo fontconfig

CFLAGS  := -Wall -Wextra -O2 \
	$(shell pkg-config --cflags $(COMMON_PKGS) $(WL_PKGS) $(X11_PKGS))
LDFLAGS := $(shell pkg-config --libs $(COMMON_PKGS) $(WL_PKGS) $(X11_PKGS))

PROTO_SRC := wlr-layer-shell-code.c xdg-shell-code.c
HEADERS   := overlay.h wlr-layer-shell-client.h xdg-shell-client.h icon_data.h bg_data.h
SRCS      := wfinfo-overlay.c overlay-render.c overlay-wayland.c overlay-x11.c $(PROTO_SRC)

wfinfo-overlay: $(SRCS) $(HEADERS)
	$(CC) -o $@ $(filter %.c,$^) $(CFLAGS) $(LDFLAGS)

debug: $(SRCS) $(HEADERS)
	$(CC) -o wfinfo-overlay -g -O0 -Wall -Wextra -Wpedantic \
		-fsanitize=address,undefined -fno-omit-frame-pointer \
		$(filter %.c,$(SRCS)) \
		$(shell pkg-config --cflags $(COMMON_PKGS) $(WL_PKGS) $(X11_PKGS)) \
		$(shell pkg-config --libs $(COMMON_PKGS) $(WL_PKGS) $(X11_PKGS)) \
		-fsanitize=address,undefined

clean:
	rm -f wfinfo-overlay

.PHONY: clean debug
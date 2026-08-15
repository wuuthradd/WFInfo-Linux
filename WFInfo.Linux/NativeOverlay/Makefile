CXX     ?= g++

PKGS    := cairo pangocairo fontconfig
CXXFLAGS := -std=c++17 -fPIC -Wall -Wextra -O2 -DVK_NO_PROTOTYPES \
	$(shell pkg-config --cflags $(PKGS))
STUBFLAGS := -std=c++17 -fPIC -Wall -Wextra -O2 -DVK_NO_PROTOTYPES
LDFLAGS := -shared $(shell pkg-config --libs $(PKGS)) -lpthread -lm
STUBLDFLAGS := -shared -lpthread -ldl
HEADERS := overlay.hpp vklayer-composite.hpp overlay-plugin.hpp

all: libwfinfo_vk.so libwfinfo_overlay.so

overlay-blend.vert.spv: overlay-blend.vert
	glslangValidator -V $< -o $@

overlay-blend.vert.inc: overlay-blend.vert.spv
	xxd -i $< > $@

overlay-blend.frag.spv: overlay-blend.frag
	glslangValidator -V $< -o $@

overlay-blend.frag.inc: overlay-blend.frag.spv
	xxd -i $< > $@

hdr-convert.comp.spv: hdr-convert.comp
	glslangValidator -V $< -o $@

hdr-convert.comp.inc: hdr-convert.comp.spv
	xxd -i $< > $@

SHADER_INCS := overlay-blend.vert.inc overlay-blend.frag.inc hdr-convert.comp.inc

libwfinfo_overlay.so: overlay-plugin.cpp vklayer-composite.cpp overlay-render.cpp \
		$(HEADERS) $(SHADER_INCS)
	$(CXX) -o $@ overlay-plugin.cpp vklayer-composite.cpp overlay-render.cpp \
		$(CXXFLAGS) $(LDFLAGS)

libwfinfo_vk.so: wfinfo-vklayer.cpp vklayer-composite.hpp overlay-plugin.hpp $(SHADER_INCS)
	$(CXX) -o $@ wfinfo-vklayer.cpp $(STUBFLAGS) $(STUBLDFLAGS)

debug: CXXFLAGS := $(filter-out -O2,$(CXXFLAGS)) -g -O0 \
	-fsanitize=address,undefined -fno-omit-frame-pointer
debug: STUBFLAGS := $(filter-out -O2,$(STUBFLAGS)) -g -O0 \
	-fsanitize=address,undefined -fno-omit-frame-pointer
debug: LDFLAGS += -fsanitize=address,undefined
debug: STUBLDFLAGS += -fsanitize=address,undefined
debug: libwfinfo_vk.so libwfinfo_overlay.so

clean:
	rm -f libwfinfo_vk.so libwfinfo_overlay.so \
		overlay-blend.vert.spv overlay-blend.vert.inc \
		overlay-blend.frag.spv overlay-blend.frag.inc \
		hdr-convert.comp.spv hdr-convert.comp.inc

.PHONY: clean debug all

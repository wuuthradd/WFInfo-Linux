CXX     ?= g++

PKGS    := cairo pangocairo fontconfig
CXXFLAGS := -std=c++17 -fPIC -Wall -Wextra -O2 -DVK_NO_PROTOTYPES \
	$(shell pkg-config --cflags $(PKGS))
LDFLAGS := -shared $(shell pkg-config --libs $(PKGS)) -lpthread -lm
SRC     := wfinfo-vklayer.cpp vklayer-composite.cpp overlay-render.cpp
HEADERS := overlay.hpp vklayer-composite.hpp

all: libwfinfo_vk.so

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

libwfinfo_vk.so: $(SRC) $(HEADERS) $(SHADER_INCS)
	$(CXX) -o $@ $(filter %.cpp,$^) $(CXXFLAGS) $(LDFLAGS)

debug: CXXFLAGS := $(filter-out -O2,$(CXXFLAGS)) -g -O0 \
	-fsanitize=address,undefined -fno-omit-frame-pointer
debug: LDFLAGS += -fsanitize=address,undefined
debug: libwfinfo_vk.so

clean:
	rm -f libwfinfo_vk.so \
		overlay-blend.vert.spv overlay-blend.vert.inc \
		overlay-blend.frag.spv overlay-blend.frag.inc \
		hdr-convert.comp.spv hdr-convert.comp.inc

.PHONY: clean debug all

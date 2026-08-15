#ifndef OVERLAY_PLUGIN_HPP
#define OVERLAY_PLUGIN_HPP

#include <vulkan/vulkan.h>

struct DeviceData;

struct OverlayPlugin {
    void (*on_warframe_instance)(void);
    void (*process_line)(const char *line);
    void (*log_visible_panels)(void);
    int  (*composite_init_pipeline)(DeviceData *dd);
    void (*composite_cleanup)(DeviceData *dd);
    VkCommandBuffer (*composite_record_overlays)(DeviceData *dd, VkImage sc_image, uint32_t sc_idx);
};

extern "C" OverlayPlugin *wfinfo_overlay_get(void);

#endif

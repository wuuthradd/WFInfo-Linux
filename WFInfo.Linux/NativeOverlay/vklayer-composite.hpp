#ifndef VKLAYER_COMPOSITE_HPP
#define VKLAYER_COMPOSITE_HPP

#include <vulkan/vulkan.h>
#include <mutex>
#include <unordered_map>
#include <vector>
#include <thread>
#include <atomic>
#include <cstdint>

/* ---- Dispatch tables ---- */

struct DeviceDispatch {
    PFN_vkDestroyDevice DestroyDevice = nullptr;
    PFN_vkGetDeviceProcAddr GetDeviceProcAddr = nullptr;
    PFN_vkGetDeviceQueue GetDeviceQueue = nullptr;
    PFN_vkQueueSubmit QueueSubmit = nullptr;
    PFN_vkQueueWaitIdle QueueWaitIdle = nullptr;
    PFN_vkDeviceWaitIdle DeviceWaitIdle = nullptr;
    PFN_vkQueuePresentKHR QueuePresentKHR = nullptr;
    PFN_vkCreateSwapchainKHR CreateSwapchainKHR = nullptr;
    PFN_vkDestroySwapchainKHR DestroySwapchainKHR = nullptr;
    PFN_vkGetSwapchainImagesKHR GetSwapchainImagesKHR = nullptr;
    PFN_vkCreateCommandPool CreateCommandPool = nullptr;
    PFN_vkDestroyCommandPool DestroyCommandPool = nullptr;
    PFN_vkAllocateCommandBuffers AllocateCommandBuffers = nullptr;
    PFN_vkFreeCommandBuffers FreeCommandBuffers = nullptr;
    PFN_vkBeginCommandBuffer BeginCommandBuffer = nullptr;
    PFN_vkEndCommandBuffer EndCommandBuffer = nullptr;
    PFN_vkCmdPipelineBarrier CmdPipelineBarrier = nullptr;
    PFN_vkCmdCopyImageToBuffer CmdCopyImageToBuffer = nullptr;
    PFN_vkCmdBindPipeline CmdBindPipeline = nullptr;
    PFN_vkCmdBindDescriptorSets CmdBindDescriptorSets = nullptr;
    PFN_vkCmdPushConstants CmdPushConstants = nullptr;
    PFN_vkCmdDraw CmdDraw = nullptr;
    PFN_vkCmdDispatch CmdDispatch = nullptr;
    PFN_vkCmdSetViewport CmdSetViewport = nullptr;
    PFN_vkCmdSetScissor CmdSetScissor = nullptr;
    PFN_vkCmdBeginRenderPass CmdBeginRenderPass = nullptr;
    PFN_vkCmdEndRenderPass CmdEndRenderPass = nullptr;
    PFN_vkCreateRenderPass CreateRenderPass = nullptr;
    PFN_vkDestroyRenderPass DestroyRenderPass = nullptr;
    PFN_vkCreateFramebuffer CreateFramebuffer = nullptr;
    PFN_vkDestroyFramebuffer DestroyFramebuffer = nullptr;
    PFN_vkCreateGraphicsPipelines CreateGraphicsPipelines = nullptr;
    PFN_vkCreateBuffer CreateBuffer = nullptr;
    PFN_vkDestroyBuffer DestroyBuffer = nullptr;
    PFN_vkCreateImage CreateImage = nullptr;
    PFN_vkDestroyImage DestroyImage = nullptr;
    PFN_vkCreateImageView CreateImageView = nullptr;
    PFN_vkDestroyImageView DestroyImageView = nullptr;
    PFN_vkAllocateMemory AllocateMemory = nullptr;
    PFN_vkFreeMemory FreeMemory = nullptr;
    PFN_vkMapMemory MapMemory = nullptr;
    PFN_vkUnmapMemory UnmapMemory = nullptr;
    PFN_vkBindBufferMemory BindBufferMemory = nullptr;
    PFN_vkBindImageMemory BindImageMemory = nullptr;
    PFN_vkGetBufferMemoryRequirements GetBufferMemoryRequirements = nullptr;
    PFN_vkGetImageMemoryRequirements GetImageMemoryRequirements = nullptr;
    PFN_vkCreateFence CreateFence = nullptr;
    PFN_vkDestroyFence DestroyFence = nullptr;
    PFN_vkWaitForFences WaitForFences = nullptr;
    PFN_vkGetFenceStatus GetFenceStatus = nullptr;
    PFN_vkResetFences ResetFences = nullptr;
    PFN_vkCreateShaderModule CreateShaderModule = nullptr;
    PFN_vkDestroyShaderModule DestroyShaderModule = nullptr;
    PFN_vkCreateComputePipelines CreateComputePipelines = nullptr;
    PFN_vkDestroyPipeline DestroyPipeline = nullptr;
    PFN_vkCreatePipelineLayout CreatePipelineLayout = nullptr;
    PFN_vkDestroyPipelineLayout DestroyPipelineLayout = nullptr;
    PFN_vkCreateDescriptorSetLayout CreateDescriptorSetLayout = nullptr;
    PFN_vkDestroyDescriptorSetLayout DestroyDescriptorSetLayout = nullptr;
    PFN_vkCreateDescriptorPool CreateDescriptorPool = nullptr;
    PFN_vkDestroyDescriptorPool DestroyDescriptorPool = nullptr;
    PFN_vkAllocateDescriptorSets AllocateDescriptorSets = nullptr;
    PFN_vkFreeDescriptorSets FreeDescriptorSets = nullptr;
    PFN_vkUpdateDescriptorSets UpdateDescriptorSets = nullptr;
    PFN_vkCreateSampler CreateSampler = nullptr;
    PFN_vkDestroySampler DestroySampler = nullptr;
    PFN_vkResetCommandBuffer ResetCommandBuffer = nullptr;
    PFN_vkCreateSemaphore CreateSemaphore = nullptr;
    PFN_vkDestroySemaphore DestroySemaphore = nullptr;
    PFN_vkGetImageSubresourceLayout GetImageSubresourceLayout = nullptr;
};

struct InstanceDispatch {
    PFN_vkDestroyInstance DestroyInstance = nullptr;
    PFN_vkGetInstanceProcAddr GetInstanceProcAddr = nullptr;
    PFN_vkEnumerateDeviceExtensionProperties EnumerateDeviceExtensionProperties = nullptr;
    PFN_vkGetPhysicalDeviceMemoryProperties GetPhysicalDeviceMemoryProperties = nullptr;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties GetPhysicalDeviceQueueFamilyProperties = nullptr;
    PFN_vkGetPhysicalDeviceFormatProperties GetPhysicalDeviceFormatProperties = nullptr;
    PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR GetPhysicalDeviceSurfaceCapabilitiesKHR = nullptr;
};

/* ---- Per-image draw resources ---- */

struct DrawResources {
    VkCommandBuffer cmd = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    VkSemaphore semaphore = VK_NULL_HANDLE;

};

/* ---- Per-swapchain state ---- */

struct SwapchainData {
    VkSwapchainKHR handle = VK_NULL_HANDLE;
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkColorSpaceKHR colorspace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    uint32_t width = 0, height = 0;
    std::vector<VkImage> images;
    std::vector<VkImageView> image_views;
    std::vector<VkFramebuffer> framebuffers;
    std::vector<DrawResources> draws;
    bool no_capture = false;
};

/* ---- Per-device state ---- */

struct DeviceData {
    VkDevice device = VK_NULL_HANDLE;
    VkPhysicalDevice phys_device = VK_NULL_HANDLE;
    DeviceDispatch dt;
    VkQueue gfx_queue = VK_NULL_HANDLE;
    uint32_t gfx_queue_family = 0;

    /* Screenshot capture */
    VkBuffer staging_buf = VK_NULL_HANDLE;
    VkDeviceMemory staging_mem = VK_NULL_HANDLE;
    VkDeviceSize staging_size = 0;
    void *staging_mapped = nullptr;
    int capture_requested = 0;
    std::atomic<int> capture_pending{0};
    int capture_broken = 0;
    std::thread capture_thread;
    bool capture_thread_valid = false;
    int capture_use_gpu_hdr = 0;
    uint32_t capture_w = 0, capture_h = 0;
    VkCommandPool capture_cmd_pool = VK_NULL_HANDLE;
    VkCommandBuffer capture_cmd_buf = VK_NULL_HANDLE;
    VkFence capture_fence = VK_NULL_HANDLE;
    VkSemaphore capture_sem = VK_NULL_HANDLE;

    /* Graphics pipeline for overlay composite */
    VkRenderPass render_pass = VK_NULL_HANDLE;

    VkPipelineLayout blend_layout = VK_NULL_HANDLE;
    VkPipeline blend_pipeline = VK_NULL_HANDLE;

    VkDescriptorSetLayout blend_ds_layout = VK_NULL_HANDLE;
    VkDescriptorPool blend_ds_pool = VK_NULL_HANDLE;
    VkSampler blend_sampler = VK_NULL_HANDLE;
    VkCommandPool composite_cmd_pool = VK_NULL_HANDLE;

    /* HDR capture: GPU compute PQ->sRGB conversion */
    VkShaderModule hdr_shader = VK_NULL_HANDLE;
    VkPipelineLayout hdr_layout = VK_NULL_HANDLE;
    VkPipeline hdr_pipeline = VK_NULL_HANDLE;
    VkDescriptorSetLayout hdr_ds_layout = VK_NULL_HANDLE;
    VkDescriptorPool hdr_ds_pool = VK_NULL_HANDLE;
    VkDescriptorSet hdr_ds = VK_NULL_HANDLE;
    VkBuffer hdr_dst_buf = VK_NULL_HANDLE;
    VkDeviceMemory hdr_dst_mem = VK_NULL_HANDLE;
    VkDeviceSize hdr_dst_size = 0;
    void *hdr_dst_mapped = nullptr;

    SwapchainData sc;
    VkPhysicalDeviceMemoryProperties mem_props{};
};

/* ---- Per-instance state ---- */

struct InstanceData {
    InstanceDispatch dt;
    VkPhysicalDevice phys_device = VK_NULL_HANDLE;
};

/* ---- Global state ---- */

extern std::mutex g_lock;
extern std::unordered_map<void*, DeviceData> g_device_map;
extern std::unordered_map<void*, InstanceData> g_instance_map;

DeviceData *get_device_data(VkDevice device);
InstanceData *get_instance_data(VkInstance instance);

/* ---- Composite functions (vklayer-composite.cpp) ---- */

int composite_init_pipeline(DeviceData *dd);
void composite_cleanup(DeviceData *dd);
VkCommandBuffer composite_record_overlays(DeviceData *dd,
                                           VkImage sc_image, uint32_t sc_idx);

uint32_t find_memory_type(const VkPhysicalDeviceMemoryProperties *props,
                          uint32_t type_bits, VkMemoryPropertyFlags flags);

void layer_log(const char *fmt, ...);

#endif
/*
 * wfinfo-vklayer.cpp - WFInfo Vulkan layer
 *
 * Implicit Vulkan layer that provides screenshot capture and overlay
 * compositing for WFInfo. Communicates with the .NET app via unix socket.
 *
 * Hooks: vkCreateInstance, vkCreateDevice, vkCreateSwapchainKHR,
 *        vkQueuePresentKHR, and corresponding destroy functions.
 *
 * Build: compiled as a shared library (libwfinfo_vk.so) with -fPIC,
 *        linked against pangocairo for overlay rendering.
 *        Does NOT link libvulkan.so (uses dispatch chain).
 */

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdarg>
#include <cstdint>
#include <ctime>
#include <unistd.h>
#include <errno.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/stat.h>
#include <poll.h>
#include <fcntl.h>
#include <signal.h>
#include <alloca.h>

#include <mutex>
#include <thread>
#include <atomic>
#include <unordered_map>
#include <vector>

#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include "vklayer-composite.hpp"
#include "overlay.hpp"

/* ---- diagnostics ---- */

void layer_log(const char *fmt, ...)
{
    static FILE *cached_fp;
    static time_t last_check;
    static std::mutex log_mutex;

    std::lock_guard<std::mutex> guard(log_mutex);

    time_t now = time(nullptr);
    if (!cached_fp || now != last_check) {
        last_check = now;

        const char *home = getenv("HOME");
        if (!home) home = "/tmp";
        char path[512];
        snprintf(path, sizeof(path), "%s/.local/share/WFInfo/vklayer.log", home);

        const char *mode = "a";
        struct stat st;
        if (stat(path, &st) == 0) {
            double age_hours = difftime(now, st.st_mtime) / 3600.0;
            if (age_hours > 12.0)
                mode = "w";
        }

        if (cached_fp && mode[0] == 'w') {
            fclose(cached_fp);
            cached_fp = nullptr;
        }
        if (!cached_fp) {
            cached_fp = fopen(path, mode);
            if (!cached_fp)
                cached_fp = fopen("/tmp/vklayer.log", mode);
        }
    }

    if (!cached_fp) return;
    struct tm tm;
    localtime_r(&now, &tm);
    fprintf(cached_fp, "[%d/%d/%d %d:%02d:%02d %s] ",
            tm.tm_mon + 1, tm.tm_mday, tm.tm_year + 1900,
            tm.tm_hour == 0 ? 12 : (tm.tm_hour > 12 ? tm.tm_hour - 12 : tm.tm_hour),
            tm.tm_min, tm.tm_sec,
            tm.tm_hour < 12 ? "AM" : "PM");
    va_list ap;
    va_start(ap, fmt);
    vfprintf(cached_fp, fmt, ap);
    va_end(ap);
    fputc('\n', cached_fp);
    fflush(cached_fp);
}

/* ---- globals ---- */

/* Build ID embedded as a searchable marker in the .so binary.
 * The .NET app extracts this to compare against a running layer. */
static const char g_build_id[] = "WFINFO_BUILD=" __DATE__ " " __TIME__;

#define SOCK_FILENAME "wfinfo_layer.sock"

static char sock_path[256];

static const char *get_sock_path(void)
{
    if (sock_path[0])
        return sock_path;
    const char *home = getenv("HOME");
    if (home && home[0])
        snprintf(sock_path, sizeof(sock_path), "%s/.local/share/WFInfo/%s", home, SOCK_FILENAME);
    else
        snprintf(sock_path, sizeof(sock_path), "/tmp/%s", SOCK_FILENAME);
    return sock_path;
}

static inline int is_10bit_format(VkFormat f)
{
    return f == VK_FORMAT_A2B10G10R10_UNORM_PACK32 ||
           f == VK_FORMAT_A2R10G10B10_UNORM_PACK32;
}

/* ---- state maps and helpers ---- */

std::mutex g_lock;
std::unordered_map<void*, DeviceData> g_device_map;
std::unordered_map<void*, InstanceData> g_instance_map;

/* When true, layer is a zero-cost passthrough (not a Wine/Proton process) */
static int g_passthrough = 0;

/* Pointer to the active renderer DeviceData in g_device_map.
 * Set during CreateDevice, cleared during DestroyDevice.
 * Used by IPC callbacks and stub backend that lack a DeviceData parameter. */
static DeviceData *g_active_dd = nullptr;

/* Passthrough dispatch (non-Warframe processes) */
static PFN_vkGetInstanceProcAddr g_passthrough_gipa = nullptr;
static PFN_vkDestroyInstance g_passthrough_destroy_instance = nullptr;

/* IPC socket state */
static int sock_listen_fd = -1;
static int sock_client_fd = -1;
static std::mutex sock_mutex;



/* ---- dispatch key extraction ---- */

static inline void *dispatch_key(const void *handle)
{
    return *(void *const *)handle;
}

DeviceData *get_device_data(VkDevice device)
{
    void *key = dispatch_key(device);
    std::lock_guard<std::mutex> lk(g_lock);
    auto it = g_device_map.find(key);
    return (it != g_device_map.end()) ? &it->second : nullptr;
}

InstanceData *get_instance_data(VkInstance instance)
{
    void *key = dispatch_key(instance);
    std::lock_guard<std::mutex> lk(g_lock);
    auto it = g_instance_map.find(key);
    return (it != g_instance_map.end()) ? &it->second : nullptr;
}

/* ---- process detection ---- */

static int is_warframe(void)
{
    if (getenv("WFINFO_VK_LAYER"))
        return 1;

    int is_wine = 0;
    char buf[256];
    ssize_t len = readlink("/proc/self/exe", buf, sizeof(buf) - 1);
    if (len > 0) {
        buf[len] = '\0';
        if (strstr(buf, "wine") || strstr(buf, "proton"))
            is_wine = 1;
    }
    if (!is_wine) {
        if (getenv("WINELOADER") || getenv("WINEPREFIX") || getenv("PROTON_VERSION"))
            is_wine = 1;
    }
    if (!is_wine)
        return 0;

    FILE *f = fopen("/proc/self/cmdline", "r");
    if (!f) return 0;
    char cmdline[1024];
    size_t n = fread(cmdline, 1, sizeof(cmdline) - 1, f);
    fclose(f);
    cmdline[n] = '\0';
    for (size_t i = 0; i < n; i++)
        if (cmdline[i] == '\0') cmdline[i] = ' ';

    if (strstr(cmdline, "Warframe.x64.exe") || strstr(cmdline, "Warframe.exe"))
        return 1;

    return 0;
}

/* ---- find memory type ---- */

uint32_t find_memory_type(const VkPhysicalDeviceMemoryProperties *props,
                          uint32_t type_bits, VkMemoryPropertyFlags flags)
{
    for (uint32_t i = 0; i < props->memoryTypeCount; i++) {
        if ((type_bits & (1u << i)) &&
            (props->memoryTypes[i].propertyFlags & flags) == flags)
            return i;
    }
    return UINT32_MAX;
}

/* ---- IPC socket ---- */

static void ipc_init(void)
{
    if (sock_listen_fd >= 0)
        return;

    const char *spath = get_sock_path();
    unlink(spath);

    sock_listen_fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (sock_listen_fd < 0) {
        fprintf(stderr, "wfinfo-vklayer: socket() failed: %s\n", strerror(errno));
        return;
    }

    struct sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, spath, sizeof(addr.sun_path) - 1);

    if (bind(sock_listen_fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        fprintf(stderr, "wfinfo-vklayer: bind(%s) failed: %s\n", spath, strerror(errno));
        close(sock_listen_fd);
        sock_listen_fd = -1;
        return;
    }

    if (listen(sock_listen_fd, 1) < 0) {
        close(sock_listen_fd);
        sock_listen_fd = -1;
        return;
    }

    fprintf(stderr, "wfinfo-vklayer: listening on %s\n", spath);
}

static void ipc_cleanup(void)
{
    if (sock_client_fd >= 0) { close(sock_client_fd); sock_client_fd = -1; }
    if (sock_listen_fd >= 0) { close(sock_listen_fd); sock_listen_fd = -1; }
    unlink(get_sock_path());
}

static void ipc_send(const void *data, size_t len)
{
    std::lock_guard<std::mutex> guard(sock_mutex);
    if (sock_client_fd < 0) return;
    size_t sent = 0;
    int retries = 0;
    while (sent < len) {
        ssize_t n = send(sock_client_fd, (const char *)data + sent, len - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            if (n < 0 && errno == EINTR) continue;
            if (n < 0 && errno == EAGAIN) {
                if (++retries > 3) {
                    fprintf(stderr, "wfinfo-vklayer: send buffer full, dropping client\n");
                    close(sock_client_fd);
                    sock_client_fd = -1;
                    break;
                }
                continue;
            }
            fprintf(stderr, "wfinfo-vklayer: client disconnected\n");
            close(sock_client_fd);
            sock_client_fd = -1;
            break;
        }
        sent += (size_t)n;
    }
}

static void ipc_send_str(const char *s)
{
    ipc_send(s, strlen(s));
}

void overlay_send_event(const char *json)
{
    ipc_send_str(json);
}

static void ipc_process_line(const char *line)
{
    if (strstr(line, "\"capture\"")) {
        layer_log("ipc_process_line: capture requested");
        if (g_active_dd)
            g_active_dd->capture_requested = 1;
    } else if (strstr(line, "\"query_info\"")) {
        const char *build = g_build_id + sizeof("WFINFO_BUILD=") - 1;
        char info[512];
        uint32_t w = 0, h = 0;
        if (g_active_dd) {
            w = g_active_dd->sc.width;
            h = g_active_dd->sc.height;
        }
        snprintf(info, sizeof(info),
            "{\"type\":\"info\",\"width\":%u,\"height\":%u,\"build\":\"%s\"}\n",
            w, h, build);
        ipc_send_str(info);
    } else if (strstr(line, "\"quit\"")) {
        if (sock_client_fd >= 0) {
            close(sock_client_fd);
            sock_client_fd = -1;
        }
    } else {
        layer_log("ipc_process_line: forwarding cmd, snapit.active=%d", snapit.active);
        process_line(line);
        for (size_t pi = 0; pi < panels.size(); pi++) {
            if (panels[pi].visible)
                layer_log("  panel[%zu] vis=1 pos=(%d,%d) %dx%d snapit=%d",
                          pi, panels[pi].x, panels[pi].y,
                          panels[pi].w, panels[pi].h, panels[pi].snapit);
        }
    }
}

static void ipc_poll(void)
{
    if (sock_client_fd < 0 && sock_listen_fd >= 0) {
        int fd = accept4(sock_listen_fd, nullptr, nullptr, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (fd >= 0) {
            sock_client_fd = fd;
            layer_log("client connected, fd=%d", fd);
            fprintf(stderr, "wfinfo-vklayer: client connected\n");
        }
    }

    if (sock_client_fd < 0) return;

    static char ipc_buf[8192];
    static int ipc_pos = 0;

    for (;;) {
        ssize_t n = read(sock_client_fd, ipc_buf + ipc_pos,
                         sizeof(ipc_buf) - (size_t)ipc_pos - 1);
        if (n <= 0) {
            if (n == 0 || (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
                if (n == 0)
                    fprintf(stderr, "wfinfo-vklayer: client disconnected\n");
                close(sock_client_fd);
                sock_client_fd = -1;
                ipc_pos = 0;
            }
            break;
        }
        ipc_pos += (int)n;
        ipc_buf[ipc_pos] = '\0';

        char *start = ipc_buf;
        char *nl;
        while ((nl = strchr(start, '\n')) != nullptr) {
            *nl = '\0';
            if (nl > start)
                ipc_process_line(start);
            start = nl + 1;
        }

        int remain = ipc_pos - (int)(start - ipc_buf);
        if (remain > 0 && start != ipc_buf)
            memmove(ipc_buf, start, (size_t)remain);
        ipc_pos = remain;
    }
}

/* ---- screenshot capture ---- */

static void ensure_staging_buffer(DeviceData *dd, VkDeviceSize needed)
{
    if (dd->staging_buf && dd->staging_size >= needed)
        return;

    if (dd->staging_mapped) {
        dd->dt.UnmapMemory(dd->device, dd->staging_mem);
        dd->staging_mapped = nullptr;
    }
    if (dd->staging_buf) dd->dt.DestroyBuffer(dd->device, dd->staging_buf, nullptr);
    if (dd->staging_mem) dd->dt.FreeMemory(dd->device, dd->staging_mem, nullptr);
    dd->staging_buf = VK_NULL_HANDLE;
    dd->staging_mem = VK_NULL_HANDLE;

    VkBufferCreateInfo bci{};
    bci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bci.size = needed;
    bci.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT | VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
    bci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (dd->dt.CreateBuffer(dd->device, &bci, nullptr, &dd->staging_buf) != VK_SUCCESS)
        return;

    VkMemoryRequirements req;
    dd->dt.GetBufferMemoryRequirements(dd->device, dd->staging_buf, &req);

    uint32_t mem_type = find_memory_type(&dd->mem_props, req.memoryTypeBits,
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (mem_type == UINT32_MAX) {
        dd->dt.DestroyBuffer(dd->device, dd->staging_buf, nullptr);
        dd->staging_buf = VK_NULL_HANDLE;
        return;
    }

    VkMemoryAllocateInfo mai{};
    mai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    mai.allocationSize = req.size;
    mai.memoryTypeIndex = mem_type;
    if (dd->dt.AllocateMemory(dd->device, &mai, nullptr, &dd->staging_mem) != VK_SUCCESS) {
        dd->dt.DestroyBuffer(dd->device, dd->staging_buf, nullptr);
        dd->staging_buf = VK_NULL_HANDLE;
        return;
    }

    if (dd->dt.BindBufferMemory(dd->device, dd->staging_buf, dd->staging_mem, 0) != VK_SUCCESS ||
        dd->dt.MapMemory(dd->device, dd->staging_mem, 0, needed, 0, &dd->staging_mapped) != VK_SUCCESS) {
        dd->dt.DestroyBuffer(dd->device, dd->staging_buf, nullptr);
        dd->dt.FreeMemory(dd->device, dd->staging_mem, nullptr);
        dd->staging_buf = VK_NULL_HANDLE;
        dd->staging_mem = VK_NULL_HANDLE;
        dd->staging_mapped = nullptr;
        return;
    }
    dd->staging_size = needed;
}

static uint32_t find_queue_family(DeviceData *dd, VkQueue queue)
{
    if (queue == dd->gfx_queue)
        return dd->gfx_queue_family;

    for (uint32_t fam = 0; fam < 16; fam++) {
        VkQueue q = VK_NULL_HANDLE;
        dd->dt.GetDeviceQueue(dd->device, fam, 0, &q);
        if (q == queue)
            return fam;
        if (q == VK_NULL_HANDLE)
            continue;
    }
    return dd->gfx_queue_family;
}

/* ---- HDR GPU conversion pipeline (lazy init) ---- */

#include "hdr-convert.comp.inc"

static int hdr_convert_init(DeviceData *dd)
{
    VkResult r;

    VkShaderModuleCreateInfo smci{};
    smci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    smci.codeSize = hdr_convert_comp_spv_len;
    smci.pCode = (const uint32_t *)hdr_convert_comp_spv;
    r = dd->dt.CreateShaderModule(dd->device, &smci, nullptr, &dd->hdr_shader);
    if (r != VK_SUCCESS) {
        layer_log("hdr_convert_init: shader module failed (%d)", r);
        return -1;
    }

    VkDescriptorSetLayoutBinding bindings[2]{};
    bindings[0].binding = 0;
    bindings[0].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
    bindings[0].descriptorCount = 1;
    bindings[0].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    bindings[1].binding = 1;
    bindings[1].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
    bindings[1].descriptorCount = 1;
    bindings[1].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;

    VkDescriptorSetLayoutCreateInfo dslci{};
    dslci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    dslci.bindingCount = 2;
    dslci.pBindings = bindings;
    r = dd->dt.CreateDescriptorSetLayout(dd->device, &dslci, nullptr, &dd->hdr_ds_layout);
    if (r != VK_SUCCESS) return -1;

    VkPushConstantRange pcr{};
    pcr.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
    pcr.offset = 0;
    pcr.size = 8;

    VkPipelineLayoutCreateInfo plci{};
    plci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    plci.setLayoutCount = 1;
    plci.pSetLayouts = &dd->hdr_ds_layout;
    plci.pushConstantRangeCount = 1;
    plci.pPushConstantRanges = &pcr;
    r = dd->dt.CreatePipelineLayout(dd->device, &plci, nullptr, &dd->hdr_layout);
    if (r != VK_SUCCESS) return -1;

    VkPipelineShaderStageCreateInfo stage{};
    stage.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    stage.module = dd->hdr_shader;
    stage.pName = "main";

    VkComputePipelineCreateInfo cpci{};
    cpci.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
    cpci.stage = stage;
    cpci.layout = dd->hdr_layout;
    r = dd->dt.CreateComputePipelines(dd->device, VK_NULL_HANDLE, 1, &cpci, nullptr,
                                       &dd->hdr_pipeline);
    if (r != VK_SUCCESS) {
        layer_log("hdr_convert_init: pipeline failed (%d)", r);
        return -1;
    }

    VkDescriptorPoolSize pool_sizes[1]{};
    pool_sizes[0].type = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
    pool_sizes[0].descriptorCount = 2;

    VkDescriptorPoolCreateInfo dpci{};
    dpci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dpci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    dpci.maxSets = 1;
    dpci.poolSizeCount = 1;
    dpci.pPoolSizes = pool_sizes;
    r = dd->dt.CreateDescriptorPool(dd->device, &dpci, nullptr, &dd->hdr_ds_pool);
    if (r != VK_SUCCESS) return -1;

    VkDescriptorSetAllocateInfo dsai{};
    dsai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    dsai.descriptorPool = dd->hdr_ds_pool;
    dsai.descriptorSetCount = 1;
    dsai.pSetLayouts = &dd->hdr_ds_layout;
    r = dd->dt.AllocateDescriptorSets(dd->device, &dsai, &dd->hdr_ds);
    if (r != VK_SUCCESS) return -1;

    layer_log("hdr_convert_init: GPU PQ->sRGB pipeline ready");
    return 0;
}

static void ensure_hdr_dst_buffer(DeviceData *dd, VkDeviceSize needed)
{
    if (dd->hdr_dst_buf && dd->hdr_dst_size >= needed)
        return;

    if (dd->hdr_dst_mapped) {
        dd->dt.UnmapMemory(dd->device, dd->hdr_dst_mem);
        dd->hdr_dst_mapped = nullptr;
    }
    if (dd->hdr_dst_buf) dd->dt.DestroyBuffer(dd->device, dd->hdr_dst_buf, nullptr);
    if (dd->hdr_dst_mem) dd->dt.FreeMemory(dd->device, dd->hdr_dst_mem, nullptr);
    dd->hdr_dst_buf = VK_NULL_HANDLE;
    dd->hdr_dst_mem = VK_NULL_HANDLE;

    VkBufferCreateInfo bci{};
    bci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bci.size = needed;
    bci.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
    bci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (dd->dt.CreateBuffer(dd->device, &bci, nullptr, &dd->hdr_dst_buf) != VK_SUCCESS)
        return;

    VkMemoryRequirements req;
    dd->dt.GetBufferMemoryRequirements(dd->device, dd->hdr_dst_buf, &req);

    uint32_t mem_type = find_memory_type(&dd->mem_props, req.memoryTypeBits,
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (mem_type == UINT32_MAX) {
        dd->dt.DestroyBuffer(dd->device, dd->hdr_dst_buf, nullptr);
        dd->hdr_dst_buf = VK_NULL_HANDLE;
        return;
    }

    VkMemoryAllocateInfo mai{};
    mai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    mai.allocationSize = req.size;
    mai.memoryTypeIndex = mem_type;
    if (dd->dt.AllocateMemory(dd->device, &mai, nullptr, &dd->hdr_dst_mem) != VK_SUCCESS) {
        dd->dt.DestroyBuffer(dd->device, dd->hdr_dst_buf, nullptr);
        dd->hdr_dst_buf = VK_NULL_HANDLE;
        return;
    }

    if (dd->dt.BindBufferMemory(dd->device, dd->hdr_dst_buf, dd->hdr_dst_mem, 0) != VK_SUCCESS ||
        dd->dt.MapMemory(dd->device, dd->hdr_dst_mem, 0, needed, 0, &dd->hdr_dst_mapped) != VK_SUCCESS) {
        dd->dt.DestroyBuffer(dd->device, dd->hdr_dst_buf, nullptr);
        dd->dt.FreeMemory(dd->device, dd->hdr_dst_mem, nullptr);
        dd->hdr_dst_buf = VK_NULL_HANDLE;
        dd->hdr_dst_mem = VK_NULL_HANDLE;
        dd->hdr_dst_mapped = nullptr;
        return;
    }
    dd->hdr_dst_size = needed;
}

/* Worker thread for capture completion: waits on fence, does CPU
 * conversion if needed, sends pixel data over IPC. Runs off the
 * present thread so QueuePresentKHR never blocks. */
static void capture_worker(DeviceData *dd);

/* Submit capture GPU work (non-blocking). Records copy + optional HDR
 * conversion, submits with semaphore chaining. Fence wait + IPC send
 * happen on a worker thread (capture_worker).
 * Returns 1 if submitted, 0 on failure. */
static int do_capture(DeviceData *dd, VkQueue queue, VkImage sc_image,
                      const VkSemaphore *wait_sems, uint32_t wait_count)
{
    uint32_t w = dd->sc.width, h = dd->sc.height;
    VkDeviceSize pixel_size = (VkDeviceSize)w * h * 4;

    if (!w || !h) {
        ipc_send_str("{\"error\":\"no swapchain\"}\n");
        return 0;
    }

    ensure_staging_buffer(dd, pixel_size);
    if (!dd->staging_buf || !dd->staging_mapped) {
        ipc_send_str("{\"error\":\"staging buffer failed\"}\n");
        return 0;
    }

    VkResult res;

    uint32_t present_family = find_queue_family(dd, queue);

    if (!dd->capture_cmd_pool) {
        VkCommandPoolCreateInfo cpci{};
        cpci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
        cpci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        cpci.queueFamilyIndex = present_family;
        res = dd->dt.CreateCommandPool(dd->device, &cpci, nullptr, &dd->capture_cmd_pool);
        if (res != VK_SUCCESS) {
            fprintf(stderr, "wfinfo-vklayer: CreateCommandPool failed: %d\n", res);
            ipc_send_str("{\"error\":\"cmd pool failed\"}\n");
            return 0;
        }
    }

    if (!dd->capture_cmd_buf) {
        VkCommandBufferAllocateInfo cbai{};
        cbai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        cbai.commandPool = dd->capture_cmd_pool;
        cbai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        cbai.commandBufferCount = 1;
        if (dd->dt.AllocateCommandBuffers(dd->device, &cbai, &dd->capture_cmd_buf) != VK_SUCCESS) {
            ipc_send_str("{\"error\":\"alloc cmd buf failed\"}\n");
            dd->capture_cmd_buf = VK_NULL_HANDLE;
            return 0;
        }
    }
    if (!dd->capture_fence) {
        VkFenceCreateInfo fci{};
        fci.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
        if (dd->dt.CreateFence(dd->device, &fci, nullptr, &dd->capture_fence) != VK_SUCCESS) {
            ipc_send_str("{\"error\":\"create fence failed\"}\n");
            dd->capture_fence = VK_NULL_HANDLE;
            return 0;
        }
    }
    if (!dd->capture_sem) {
        VkSemaphoreCreateInfo sci{};
        sci.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        if (dd->dt.CreateSemaphore(dd->device, &sci, nullptr, &dd->capture_sem) != VK_SUCCESS) {
            ipc_send_str("{\"error\":\"create semaphore failed\"}\n");
            dd->capture_sem = VK_NULL_HANDLE;
            return 0;
        }
    }

    VkCommandBufferBeginInfo cbbi{};
    cbbi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    cbbi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;

    dd->dt.ResetCommandBuffer(dd->capture_cmd_buf, 0);
    dd->dt.BeginCommandBuffer(dd->capture_cmd_buf, &cbbi);

    /* Transition swapchain image: PRESENT_SRC -> GENERAL.
     * GENERAL is safe on all hardware and avoids driver-specific
     * decompression paths that TRANSFER_SRC_OPTIMAL can trigger. */
    VkImageMemoryBarrier barrier{};
    barrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    barrier.srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.image = sc_image;
    barrier.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };

    dd->dt.CmdPipelineBarrier(dd->capture_cmd_buf,
        VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
        0, 0, nullptr, 0, nullptr, 1, &barrier);

    VkBufferImageCopy region{};
    region.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
    region.imageExtent = { w, h, 1 };
    dd->dt.CmdCopyImageToBuffer(dd->capture_cmd_buf, sc_image,
        VK_IMAGE_LAYOUT_GENERAL, dd->staging_buf, 1, &region);

    /* Transition back: GENERAL -> PRESENT_SRC */
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
    barrier.oldLayout = VK_IMAGE_LAYOUT_GENERAL;
    barrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    dd->dt.CmdPipelineBarrier(dd->capture_cmd_buf,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
        0, 0, nullptr, 0, nullptr, 1, &barrier);

    /* HDR PQ: chain GPU compute conversion into the same command buffer.
     * Converts A2B10G10R10 PQ pixels in staging_buf to B8G8R8A8 sRGB in hdr_dst_buf. */
    int use_gpu_hdr = 0;
    if (is_10bit_format(dd->sc.format) &&
        dd->sc.colorspace == VK_COLOR_SPACE_HDR10_ST2084_EXT) {

        if (!dd->hdr_pipeline && hdr_convert_init(dd) != 0) {
            layer_log("do_capture: HDR pipeline init failed, falling back to CPU");
        }
        if (dd->hdr_pipeline) {
            ensure_hdr_dst_buffer(dd, pixel_size);
            if (dd->hdr_dst_buf && dd->hdr_dst_mapped) {
                use_gpu_hdr = 1;

                VkBufferMemoryBarrier buf_bar{};
                buf_bar.sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER;
                buf_bar.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
                buf_bar.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
                buf_bar.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                buf_bar.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                buf_bar.buffer = dd->staging_buf;
                buf_bar.offset = 0;
                buf_bar.size = pixel_size;
                dd->dt.CmdPipelineBarrier(dd->capture_cmd_buf,
                    VK_PIPELINE_STAGE_TRANSFER_BIT,
                    VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                    0, 0, nullptr, 1, &buf_bar, 0, nullptr);

                VkDescriptorBufferInfo src_info{};
                src_info.buffer = dd->staging_buf;
                src_info.offset = 0;
                src_info.range = pixel_size;

                VkDescriptorBufferInfo dst_info{};
                dst_info.buffer = dd->hdr_dst_buf;
                dst_info.offset = 0;
                dst_info.range = pixel_size;

                VkWriteDescriptorSet writes[2]{};
                writes[0].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                writes[0].dstSet = dd->hdr_ds;
                writes[0].dstBinding = 0;
                writes[0].descriptorCount = 1;
                writes[0].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
                writes[0].pBufferInfo = &src_info;
                writes[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                writes[1].dstSet = dd->hdr_ds;
                writes[1].dstBinding = 1;
                writes[1].descriptorCount = 1;
                writes[1].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
                writes[1].pBufferInfo = &dst_info;
                dd->dt.UpdateDescriptorSets(dd->device, 2, writes, 0, nullptr);

                dd->dt.CmdBindPipeline(dd->capture_cmd_buf,
                    VK_PIPELINE_BIND_POINT_COMPUTE, dd->hdr_pipeline);
                dd->dt.CmdBindDescriptorSets(dd->capture_cmd_buf,
                    VK_PIPELINE_BIND_POINT_COMPUTE, dd->hdr_layout,
                    0, 1, &dd->hdr_ds, 0, nullptr);

                uint32_t pc[2];
                pc[0] = w * h;
                pc[1] = (dd->sc.format == VK_FORMAT_A2B10G10R10_UNORM_PACK32) ? 1 : 0;
                dd->dt.CmdPushConstants(dd->capture_cmd_buf, dd->hdr_layout,
                    VK_SHADER_STAGE_COMPUTE_BIT, 0, 8, pc);

                uint32_t groups = (w * h + 255) / 256;
                dd->dt.CmdDispatch(dd->capture_cmd_buf, groups, 1, 1);

                VkBufferMemoryBarrier dst_bar{};
                dst_bar.sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER;
                dst_bar.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
                dst_bar.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
                dst_bar.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                dst_bar.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                dst_bar.buffer = dd->hdr_dst_buf;
                dst_bar.offset = 0;
                dst_bar.size = pixel_size;
                dd->dt.CmdPipelineBarrier(dd->capture_cmd_buf,
                    VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                    VK_PIPELINE_STAGE_HOST_BIT,
                    0, 0, nullptr, 1, &dst_bar, 0, nullptr);
            }
        }
    }

    dd->dt.EndCommandBuffer(dd->capture_cmd_buf);

    auto *cap_wait_stages = static_cast<VkPipelineStageFlags *>(
        alloca(sizeof(VkPipelineStageFlags) * wait_count));
    for (uint32_t i = 0; i < wait_count; i++)
        cap_wait_stages[i] = VK_PIPELINE_STAGE_TRANSFER_BIT;

    VkSubmitInfo si{};
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = wait_count;
    si.pWaitSemaphores = wait_sems;
    si.pWaitDstStageMask = cap_wait_stages;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &dd->capture_cmd_buf;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &dd->capture_sem;

    dd->dt.ResetFences(dd->device, 1, &dd->capture_fence);
    res = dd->dt.QueueSubmit(queue, 1, &si, dd->capture_fence);
    if (res != VK_SUCCESS) {
        fprintf(stderr, "wfinfo-vklayer: QueueSubmit failed: %d\n", res);
        ipc_send_str("{\"error\":\"queue submit failed\"}\n");
        return 0;
    }

    dd->capture_pending.store(1, std::memory_order_release);
    dd->capture_use_gpu_hdr = use_gpu_hdr;
    dd->capture_w = w;
    dd->capture_h = h;

    if (dd->capture_thread_valid) {
        dd->capture_thread.join();
        dd->capture_thread_valid = false;
    }

    try {
        dd->capture_thread = std::thread(capture_worker, dd);
        dd->capture_thread_valid = true;
    } catch (...) {
        dd->dt.WaitForFences(dd->device, 1, &dd->capture_fence, VK_TRUE, 5000000000ULL);
        capture_worker(dd);
    }
    return 1;
}

static void capture_worker(DeviceData *dd)
{
    VkResult res = dd->dt.WaitForFences(dd->device, 1, &dd->capture_fence,
                                         VK_TRUE, 5000000000ULL);
    if (res != VK_SUCCESS) {
        fprintf(stderr, "wfinfo-vklayer: capture fence error: %d\n", res);
        layer_log("capture fence wait failed: %d, cmd buffer still in-flight", res);
        dd->capture_broken = 1;
        ipc_send_str("{\"error\":\"fence wait failed\"}\n");
        return;
    }

    uint32_t w = dd->capture_w, h = dd->capture_h;

    /* CPU fallback for 10-bit non-PQ formats (simple truncation) */
    if (!dd->capture_use_gpu_hdr && is_10bit_format(dd->sc.format)) {
        auto *pixels = static_cast<uint32_t *>(dd->staging_mapped);
        uint32_t count = w * h;
        int is_abgr = (dd->sc.format == VK_FORMAT_A2B10G10R10_UNORM_PACK32);
        for (uint32_t i = 0; i < count; i++) {
            uint32_t p = pixels[i];
            uint32_t c0 = (p >>  0) & 0x3FF;
            uint32_t c1 = (p >> 10) & 0x3FF;
            uint32_t c2 = (p >> 20) & 0x3FF;
            uint8_t r8 = (uint8_t)((is_abgr ? c0 : c2) >> 2);
            uint8_t g8 = (uint8_t)(c1 >> 2);
            uint8_t b8 = (uint8_t)((is_abgr ? c2 : c0) >> 2);
            pixels[i] = (uint32_t)b8 | ((uint32_t)g8 << 8) |
                        ((uint32_t)r8 << 16) | (0xFFu << 24);
        }
    }

    const void *send_buf = dd->capture_use_gpu_hdr
        ? dd->hdr_dst_mapped : dd->staging_mapped;

    int stride = (int)(w * 4);
    const char *fmt_str = "bgra8888";
    switch (dd->sc.format) {
        case VK_FORMAT_R8G8B8A8_UNORM:
        case VK_FORMAT_R8G8B8A8_SRGB:
            fmt_str = "rgba8888";
            break;
        default:
            fmt_str = "bgra8888";
            break;
    }
    char header[256];
    snprintf(header, sizeof(header),
        "{\"width\":%u,\"height\":%u,\"stride\":%d,\"format\":\"%s\",\"size\":%u}\n",
        w, h, stride, fmt_str, (unsigned)(w * h * 4));

    VkDeviceSize pixel_size = (VkDeviceSize)w * h * 4;
    layer_log("do_capture: sending header + %u bytes (gpu_hdr=%d)",
              (unsigned)pixel_size, dd->capture_use_gpu_hdr);

    {
        std::lock_guard<std::mutex> guard(sock_mutex);
        if (sock_client_fd >= 0) {
            size_t hlen = strlen(header);
            size_t sent = 0;
            while (sent < hlen) {
                ssize_t n = send(sock_client_fd, header + sent, hlen - sent, MSG_NOSIGNAL);
                if (n > 0) { sent += (size_t)n; continue; }
                if (n < 0 && errno == EINTR) continue;
                close(sock_client_fd); sock_client_fd = -1;
                break;
            }
            if (sock_client_fd >= 0) {
                sent = 0;
                while (sent < (size_t)pixel_size) {
                    ssize_t n = send(sock_client_fd, (const char *)send_buf + sent,
                                     (size_t)pixel_size - sent, MSG_NOSIGNAL);
                    if (n > 0) { sent += (size_t)n; continue; }
                    if (n < 0 && errno == EINTR) continue;
                    if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
                        struct pollfd pfd{};
                        pfd.fd = sock_client_fd;
                        pfd.events = POLLOUT;
                        if (poll(&pfd, 1, 3000) <= 0) {
                            layer_log("capture bulk: poll timeout, sent %zu/%zu",
                                      sent, (size_t)pixel_size);
                            break;
                        }
                        continue;
                    }
                    close(sock_client_fd); sock_client_fd = -1;
                    break;
                }
            }
        }
    }
    layer_log("do_capture: done");
    fprintf(stderr, "wfinfo-vklayer: captured %ux%u\n", w, h);

    dd->capture_pending.store(0, std::memory_order_release);
}

/* ---- stub backend callbacks ---- */

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

/* ---- Vulkan layer dispatch chain ---- */

static VKAPI_ATTR VkResult VKAPI_CALL
layer_CreateInstance(const VkInstanceCreateInfo *pCreateInfo,
                     const VkAllocationCallbacks *pAllocator,
                     VkInstance *pInstance)
{
    auto *chain = const_cast<VkLayerInstanceCreateInfo *>(
        reinterpret_cast<const VkLayerInstanceCreateInfo *>(pCreateInfo->pNext));
    while (chain &&
           !(chain->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO &&
             chain->function == VK_LAYER_LINK_INFO))
        chain = const_cast<VkLayerInstanceCreateInfo *>(
            reinterpret_cast<const VkLayerInstanceCreateInfo *>(chain->pNext));

    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;

    PFN_vkGetInstanceProcAddr next_gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;

    auto create_inst = reinterpret_cast<PFN_vkCreateInstance>(
        next_gipa(VK_NULL_HANDLE, "vkCreateInstance"));
    if (!create_inst) return VK_ERROR_INITIALIZATION_FAILED;

    VkResult res = create_inst(pCreateInfo, pAllocator, pInstance);
    if (res != VK_SUCCESS) return res;

    layer_log("layer_CreateInstance called, pid=%d", getpid());
    layer_log("  XDG_RUNTIME_DIR=%s", getenv("XDG_RUNTIME_DIR") ?: "(unset)");
    layer_log("  HOME=%s", getenv("HOME") ?: "(unset)");

    if (!is_warframe()) {
        layer_log("  not Warframe, passthrough");
        g_passthrough = 1;
        g_passthrough_gipa = next_gipa;
        g_passthrough_destroy_instance =
            reinterpret_cast<PFN_vkDestroyInstance>(next_gipa(*pInstance, "vkDestroyInstance"));
        return VK_SUCCESS;
    }

    void *key = dispatch_key(*pInstance);
    {
        std::lock_guard<std::mutex> lk(g_lock);
        auto &inst = g_instance_map[key];
        inst.dt.GetInstanceProcAddr = next_gipa;
        inst.dt.DestroyInstance =
            reinterpret_cast<PFN_vkDestroyInstance>(next_gipa(*pInstance, "vkDestroyInstance"));
        inst.dt.EnumerateDeviceExtensionProperties =
            reinterpret_cast<PFN_vkEnumerateDeviceExtensionProperties>(
                next_gipa(*pInstance, "vkEnumerateDeviceExtensionProperties"));
        inst.dt.GetPhysicalDeviceMemoryProperties =
            reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(
                next_gipa(*pInstance, "vkGetPhysicalDeviceMemoryProperties"));
        inst.dt.GetPhysicalDeviceQueueFamilyProperties =
            reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
                next_gipa(*pInstance, "vkGetPhysicalDeviceQueueFamilyProperties"));
        inst.dt.GetPhysicalDeviceFormatProperties =
            reinterpret_cast<PFN_vkGetPhysicalDeviceFormatProperties>(
                next_gipa(*pInstance, "vkGetPhysicalDeviceFormatProperties"));
        inst.dt.GetPhysicalDeviceSurfaceCapabilitiesKHR =
            reinterpret_cast<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>(
                next_gipa(*pInstance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"));
    }

    backend = &stub_backend;

    layer_log("  Warframe detected, IPC deferred to swapchain creation");
    fprintf(stderr, "wfinfo-vklayer: instance created (Warframe detected)\n");
    return VK_SUCCESS;
}

static VKAPI_ATTR void VKAPI_CALL
layer_DestroyInstance(VkInstance instance, const VkAllocationCallbacks *pAllocator)
{
    if (g_passthrough) {
        if (g_passthrough_destroy_instance)
            g_passthrough_destroy_instance(instance, pAllocator);
        return;
    }

    void *key = dispatch_key(instance);
    PFN_vkDestroyInstance destroy_fn = nullptr;
    {
        std::lock_guard<std::mutex> lk(g_lock);
        auto it = g_instance_map.find(key);
        if (it != g_instance_map.end()) {
            destroy_fn = it->second.dt.DestroyInstance;
            g_instance_map.erase(it);
        }
    }
    if (destroy_fn)
        destroy_fn(instance, pAllocator);
}

static VKAPI_ATTR VkResult VKAPI_CALL
layer_CreateDevice(VkPhysicalDevice physDev, const VkDeviceCreateInfo *pCreateInfo,
                    const VkAllocationCallbacks *pAllocator, VkDevice *pDevice)
{
    auto *chain = const_cast<VkLayerDeviceCreateInfo *>(
        reinterpret_cast<const VkLayerDeviceCreateInfo *>(pCreateInfo->pNext));
    while (chain &&
           !(chain->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO &&
             chain->function == VK_LAYER_LINK_INFO))
        chain = const_cast<VkLayerDeviceCreateInfo *>(
            reinterpret_cast<const VkLayerDeviceCreateInfo *>(chain->pNext));

    if (!chain) return VK_ERROR_INITIALIZATION_FAILED;

    PFN_vkGetInstanceProcAddr next_gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr next_gdpa = chain->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;

    /* Look up vkCreateDevice via instance (not VK_NULL_HANDLE) per Vulkan spec.
     * Passthrough processes may not have an instance stored. */
    VkInstance lookup_inst = VK_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> ilk(g_lock);
        for (auto &pair : g_instance_map) {
            lookup_inst = reinterpret_cast<VkInstance>(pair.first);
            break;
        }
    }
    auto create_dev = reinterpret_cast<PFN_vkCreateDevice>(
        next_gipa(lookup_inst, "vkCreateDevice"));
    if (!create_dev) return VK_ERROR_INITIALIZATION_FAILED;

    VkResult res = create_dev(physDev, pCreateInfo, pAllocator, pDevice);
    if (res != VK_SUCCESS) return res;

    if (g_passthrough) {
        void *key = dispatch_key(*pDevice);
        std::lock_guard<std::mutex> lk(g_lock);
        auto &dd = g_device_map[key];
        dd.device = *pDevice;
        dd.dt.DestroyDevice = reinterpret_cast<PFN_vkDestroyDevice>(
            next_gdpa(*pDevice, "vkDestroyDevice"));
        dd.dt.GetDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
            next_gdpa(*pDevice, "vkGetDeviceProcAddr"));
        return VK_SUCCESS;
    }

    void *key = dispatch_key(*pDevice);
    std::lock_guard<std::mutex> lk(g_lock);

    if (g_active_dd) {
        layer_log("CreateDevice: already initialized, extra device %p passthrough", (void*)*pDevice);
        auto &dd = g_device_map[key];
        dd.device = *pDevice;
        dd.dt.DestroyDevice = reinterpret_cast<PFN_vkDestroyDevice>(
            next_gdpa(*pDevice, "vkDestroyDevice"));
        dd.dt.GetDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
            next_gdpa(*pDevice, "vkGetDeviceProcAddr"));
        return VK_SUCCESS;
    }

    auto &dd = g_device_map[key];
    dd.device = *pDevice;
    dd.phys_device = physDev;

    #define LOAD_DEV(name) dd.dt.name = reinterpret_cast<PFN_vk##name>( \
        next_gdpa(*pDevice, "vk" #name))
    LOAD_DEV(DestroyDevice);
    LOAD_DEV(GetDeviceProcAddr);
    LOAD_DEV(GetDeviceQueue);
    LOAD_DEV(QueueSubmit);
    LOAD_DEV(QueueWaitIdle);
    LOAD_DEV(DeviceWaitIdle);
    LOAD_DEV(QueuePresentKHR);
    LOAD_DEV(CreateSwapchainKHR);
    LOAD_DEV(DestroySwapchainKHR);
    LOAD_DEV(GetSwapchainImagesKHR);
    LOAD_DEV(CreateCommandPool);
    LOAD_DEV(DestroyCommandPool);
    LOAD_DEV(AllocateCommandBuffers);
    LOAD_DEV(FreeCommandBuffers);
    LOAD_DEV(BeginCommandBuffer);
    LOAD_DEV(EndCommandBuffer);
    LOAD_DEV(CmdPipelineBarrier);
    LOAD_DEV(CmdCopyImageToBuffer);
    LOAD_DEV(CmdBindPipeline);
    LOAD_DEV(CmdBindDescriptorSets);
    LOAD_DEV(CmdPushConstants);
    LOAD_DEV(CmdDraw);
    LOAD_DEV(CmdDispatch);
    LOAD_DEV(CmdSetViewport);
    LOAD_DEV(CmdSetScissor);
    LOAD_DEV(CmdBeginRenderPass);
    LOAD_DEV(CmdEndRenderPass);
    LOAD_DEV(CreateRenderPass);
    LOAD_DEV(DestroyRenderPass);
    LOAD_DEV(CreateFramebuffer);
    LOAD_DEV(DestroyFramebuffer);
    LOAD_DEV(CreateGraphicsPipelines);
    LOAD_DEV(CreateBuffer);
    LOAD_DEV(DestroyBuffer);
    LOAD_DEV(CreateImage);
    LOAD_DEV(DestroyImage);
    LOAD_DEV(CreateImageView);
    LOAD_DEV(DestroyImageView);
    LOAD_DEV(AllocateMemory);
    LOAD_DEV(FreeMemory);
    LOAD_DEV(MapMemory);
    LOAD_DEV(UnmapMemory);
    LOAD_DEV(BindBufferMemory);
    LOAD_DEV(BindImageMemory);
    LOAD_DEV(GetBufferMemoryRequirements);
    LOAD_DEV(GetImageMemoryRequirements);
    LOAD_DEV(GetImageSubresourceLayout);
    LOAD_DEV(CreateFence);
    LOAD_DEV(DestroyFence);
    LOAD_DEV(WaitForFences);
    LOAD_DEV(GetFenceStatus);
    LOAD_DEV(ResetFences);
    LOAD_DEV(CreateShaderModule);
    LOAD_DEV(DestroyShaderModule);
    LOAD_DEV(CreateComputePipelines);
    LOAD_DEV(DestroyPipeline);
    LOAD_DEV(CreatePipelineLayout);
    LOAD_DEV(DestroyPipelineLayout);
    LOAD_DEV(CreateDescriptorSetLayout);
    LOAD_DEV(DestroyDescriptorSetLayout);
    LOAD_DEV(CreateDescriptorPool);
    LOAD_DEV(DestroyDescriptorPool);
    LOAD_DEV(AllocateDescriptorSets);
    LOAD_DEV(FreeDescriptorSets);
    LOAD_DEV(UpdateDescriptorSets);
    LOAD_DEV(CreateSampler);
    LOAD_DEV(DestroySampler);
    LOAD_DEV(ResetCommandBuffer);
    LOAD_DEV(CreateSemaphore);
    LOAD_DEV(DestroySemaphore);
    #undef LOAD_DEV

    /* Look up instance data to query physical device properties */
    InstanceData *inst = nullptr;
    for (auto &pair : g_instance_map) {
        inst = &pair.second;
        break;
    }

    if (inst) {
        inst->dt.GetPhysicalDeviceMemoryProperties(physDev, &dd.mem_props);

        uint32_t qf_count = 0;
        inst->dt.GetPhysicalDeviceQueueFamilyProperties(physDev, &qf_count, nullptr);
        std::vector<VkQueueFamilyProperties> qf_props(qf_count);
        inst->dt.GetPhysicalDeviceQueueFamilyProperties(physDev, &qf_count, qf_props.data());
        for (uint32_t i = 0; i < qf_count; i++) {
            if (qf_props[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
                dd.gfx_queue_family = i;
                break;
            }
        }
        inst->phys_device = physDev;
    }

    dd.dt.GetDeviceQueue(dd.device, dd.gfx_queue_family, 0, &dd.gfx_queue);
    g_active_dd = &dd;

    load_icons();

    fprintf(stderr, "wfinfo-vklayer: device created, queue family=%u\n",
            dd.gfx_queue_family);
    return VK_SUCCESS;
}

static VKAPI_ATTR void VKAPI_CALL
layer_DestroyDevice(VkDevice device, const VkAllocationCallbacks *pAllocator)
{
    void *key = dispatch_key(device);
    std::lock_guard<std::mutex> lk(g_lock);
    auto it = g_device_map.find(key);
    if (it == g_device_map.end()) return;

    DeviceData &dd = it->second;

    if (&dd != g_active_dd) {
        PFN_vkDestroyDevice fn = dd.dt.DestroyDevice;
        g_device_map.erase(it);
        if (fn) fn(device, pAllocator);
        return;
    }

    if (dd.capture_thread_valid) {
        dd.capture_thread.join();
        dd.capture_thread_valid = false;
    }

    composite_cleanup(&dd);

    if (dd.staging_mapped)
        dd.dt.UnmapMemory(dd.device, dd.staging_mem);
    if (dd.staging_buf) dd.dt.DestroyBuffer(dd.device, dd.staging_buf, nullptr);
    if (dd.staging_mem) dd.dt.FreeMemory(dd.device, dd.staging_mem, nullptr);
    if (dd.capture_fence) dd.dt.DestroyFence(dd.device, dd.capture_fence, nullptr);
    if (dd.capture_sem) dd.dt.DestroySemaphore(dd.device, dd.capture_sem, nullptr);
    if (dd.capture_cmd_pool) dd.dt.DestroyCommandPool(dd.device, dd.capture_cmd_pool, nullptr);

    if (dd.hdr_dst_mapped) dd.dt.UnmapMemory(dd.device, dd.hdr_dst_mem);
    if (dd.hdr_dst_buf) dd.dt.DestroyBuffer(dd.device, dd.hdr_dst_buf, nullptr);
    if (dd.hdr_dst_mem) dd.dt.FreeMemory(dd.device, dd.hdr_dst_mem, nullptr);
    if (dd.hdr_ds_pool) dd.dt.DestroyDescriptorPool(dd.device, dd.hdr_ds_pool, nullptr);
    if (dd.hdr_pipeline) dd.dt.DestroyPipeline(dd.device, dd.hdr_pipeline, nullptr);
    if (dd.hdr_layout) dd.dt.DestroyPipelineLayout(dd.device, dd.hdr_layout, nullptr);
    if (dd.hdr_ds_layout) dd.dt.DestroyDescriptorSetLayout(dd.device, dd.hdr_ds_layout, nullptr);
    if (dd.hdr_shader) dd.dt.DestroyShaderModule(dd.device, dd.hdr_shader, nullptr);

    ipc_cleanup();

    PFN_vkDestroyDevice fn = dd.dt.DestroyDevice;
    g_active_dd = nullptr;
    g_device_map.erase(it);
    if (fn) fn(device, pAllocator);
}

static VKAPI_ATTR VkResult VKAPI_CALL
layer_CreateSwapchainKHR(VkDevice device,
                          const VkSwapchainCreateInfoKHR *pCreateInfo,
                          const VkAllocationCallbacks *pAllocator,
                          VkSwapchainKHR *pSwapchain)
{
    DeviceData *dd = g_active_dd;
    if (!dd) {
        auto *fdd = get_device_data(device);
        if (fdd && fdd->dt.CreateSwapchainKHR)
            return fdd->dt.CreateSwapchainKHR(device, pCreateInfo, pAllocator, pSwapchain);
        return VK_ERROR_DEVICE_LOST;
    }

    layer_log("layer_CreateSwapchainKHR called: device=%p format=%u usage=0x%x",
              (void*)device, pCreateInfo->imageFormat, pCreateInfo->imageUsage);
    VkSwapchainCreateInfoKHR modified = *pCreateInfo;

    /* Query surface capabilities for supported usage flags */
    VkImageUsageFlags supported_usage = 0xFFFFFFFF;
    InstanceData *inst = nullptr;
    {
        std::lock_guard<std::mutex> ilk(g_lock);
        for (auto &pair : g_instance_map) { inst = &pair.second; break; }
    }
    if (inst && inst->dt.GetPhysicalDeviceSurfaceCapabilitiesKHR &&
        dd->phys_device && pCreateInfo->surface) {
        VkSurfaceCapabilitiesKHR caps{};
        VkResult cap_res = inst->dt.GetPhysicalDeviceSurfaceCapabilitiesKHR(
            dd->phys_device, pCreateInfo->surface, &caps);
        if (cap_res == VK_SUCCESS) {
            supported_usage = caps.supportedUsageFlags;
            layer_log("surface supported usage: 0x%x", supported_usage);
        }
    }

    /* TRANSFER_SRC for screenshot capture */
    int can_transfer_src = (supported_usage & VK_IMAGE_USAGE_TRANSFER_SRC_BIT) != 0;
    if (can_transfer_src)
        modified.imageUsage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT;

    /* COLOR_ATTACHMENT + INPUT_ATTACHMENT for render pass overlay compositing (DCC safe) */
    int can_color_attachment = (supported_usage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) != 0;
    if (can_color_attachment)
        modified.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    if (supported_usage & VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT)
        modified.imageUsage |= VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT;

    dd->sc.no_capture = !can_transfer_src;
    if (!can_transfer_src)
        layer_log("TRANSFER_SRC not supported by surface, screenshot capture disabled");
    if (!can_color_attachment)
        layer_log("COLOR_ATTACHMENT not supported, overlay compositing disabled");

    VkResult res = dd->dt.CreateSwapchainKHR(device, &modified, pAllocator, pSwapchain);
    if (res != VK_SUCCESS) return res;

    /* Clean up old swapchain resources on recreation */
    if (dd->sc.handle != VK_NULL_HANDLE) {
        layer_log("swapchain recreation: cleaning up old handle %p before new %p",
                  (void*)(uintptr_t)dd->sc.handle, (void*)(uintptr_t)*pSwapchain);

        if (dd->capture_thread_valid) {
            dd->capture_thread.join();
            dd->capture_thread_valid = false;
        }
        dd->dt.DeviceWaitIdle(dd->device);
        dd->capture_pending.store(0, std::memory_order_release);
        dd->capture_broken = 0;

        for (auto &fb : dd->sc.framebuffers)
            if (fb) dd->dt.DestroyFramebuffer(device, fb, nullptr);
        for (auto &iv : dd->sc.image_views)
            if (iv) dd->dt.DestroyImageView(device, iv, nullptr);
        for (auto &dr : dd->sc.draws) {
            if (dr.fence) dd->dt.DestroyFence(dd->device, dr.fence, nullptr);
            if (dr.semaphore) dd->dt.DestroySemaphore(dd->device, dr.semaphore, nullptr);
        }
        if (dd->composite_cmd_pool) {
            dd->dt.DestroyCommandPool(dd->device, dd->composite_cmd_pool, nullptr);
            dd->composite_cmd_pool = VK_NULL_HANDLE;
        }
        dd->sc = SwapchainData{};
    }

    dd->sc.handle = *pSwapchain;
    dd->sc.format = pCreateInfo->imageFormat;
    dd->sc.colorspace = pCreateInfo->imageColorSpace;
    dd->sc.width = pCreateInfo->imageExtent.width;
    dd->sc.height = pCreateInfo->imageExtent.height;

    uint32_t img_count = 0;
    VkResult img_res = dd->dt.GetSwapchainImagesKHR(device, *pSwapchain, &img_count, nullptr);
    if (img_res != VK_SUCCESS || img_count == 0) {
        layer_log("GetSwapchainImagesKHR failed (%d), disabling capture and overlay", img_res);
        dd->sc.no_capture = true;
        goto sc_done;
    }
    dd->sc.images.resize(img_count);
    img_res = dd->dt.GetSwapchainImagesKHR(device, *pSwapchain, &img_count, dd->sc.images.data());
    if (img_res != VK_SUCCESS && img_res != VK_INCOMPLETE) {
        layer_log("GetSwapchainImagesKHR (fetch) failed (%d)", img_res);
        dd->sc.images.clear();
        dd->sc.no_capture = true;
        goto sc_done;
    }

    dd->sc.image_views.resize(img_count, VK_NULL_HANDLE);
    dd->sc.framebuffers.resize(img_count, VK_NULL_HANDLE);
    dd->sc.draws.resize(img_count);

    for (uint32_t i = 0; i < img_count; i++) {
        VkImageViewCreateInfo ivci{};
        ivci.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        ivci.image = dd->sc.images[i];
        ivci.viewType = VK_IMAGE_VIEW_TYPE_2D;
        ivci.format = pCreateInfo->imageFormat;
        ivci.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        VkResult iv_res = dd->dt.CreateImageView(device, &ivci, nullptr, &dd->sc.image_views[i]);
        if (iv_res != VK_SUCCESS) {
            layer_log("CreateImageView failed for image %u (%d)", i, iv_res);
            dd->sc.image_views[i] = VK_NULL_HANDLE;
        }
    }

    if (can_color_attachment && !dd->blend_pipeline)
        composite_init_pipeline(dd);

    /* Create framebuffers per swapchain image (requires render_pass from pipeline init) */
    if (dd->render_pass) {
        for (uint32_t i = 0; i < img_count; i++) {
            if (!dd->sc.image_views[i]) continue;
            VkFramebufferCreateInfo fbci{};
            fbci.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
            fbci.renderPass = dd->render_pass;
            fbci.attachmentCount = 1;
            fbci.pAttachments = &dd->sc.image_views[i];
            fbci.width = dd->sc.width;
            fbci.height = dd->sc.height;
            fbci.layers = 1;
            dd->dt.CreateFramebuffer(dd->device, &fbci, nullptr, &dd->sc.framebuffers[i]);
        }
    }

sc_done:
    if (sock_listen_fd < 0) {
        layer_log("first swapchain, initializing IPC at %s", get_sock_path());
        ipc_init();
        layer_log("ipc_init done, listen_fd=%d", sock_listen_fd);
    }

    layer_log("swapchain created: %ux%u format=%u colorspace=%u images=%u capture=%s overlay=%s",
              dd->sc.width, dd->sc.height, dd->sc.format,
              (unsigned)pCreateInfo->imageColorSpace, img_count,
              dd->sc.no_capture ? "off" : "on",
              dd->blend_pipeline ? "on" : "off");
    fprintf(stderr, "wfinfo-vklayer: swapchain %ux%u format=%u colorspace=%u images=%u\n",
            dd->sc.width, dd->sc.height, dd->sc.format,
            (unsigned)pCreateInfo->imageColorSpace, img_count);
    return VK_SUCCESS;
}

static VKAPI_ATTR void VKAPI_CALL
layer_DestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain,
                           const VkAllocationCallbacks *pAllocator)
{
    DeviceData *dd = g_active_dd;
    if (dd && dd->sc.handle == swapchain) {
        if (dd->capture_thread_valid) {
            dd->capture_thread.join();
            dd->capture_thread_valid = false;
        }
        dd->dt.DeviceWaitIdle(dd->device);
        dd->capture_pending.store(0, std::memory_order_release);
        dd->capture_broken = 0;

        for (auto &fb : dd->sc.framebuffers)
            if (fb) dd->dt.DestroyFramebuffer(device, fb, nullptr);
        for (auto &iv : dd->sc.image_views)
            if (iv) dd->dt.DestroyImageView(device, iv, nullptr);
        for (auto &dr : dd->sc.draws) {
            if (dr.fence) dd->dt.DestroyFence(dd->device, dr.fence, nullptr);
            if (dr.semaphore) dd->dt.DestroySemaphore(dd->device, dr.semaphore, nullptr);
        }
        if (dd->composite_cmd_pool) {
            dd->dt.DestroyCommandPool(dd->device, dd->composite_cmd_pool, nullptr);
            dd->composite_cmd_pool = VK_NULL_HANDLE;
        }
        dd->sc = SwapchainData{};
    }
    if (dd && dd->dt.DestroySwapchainKHR)
        dd->dt.DestroySwapchainKHR(device, swapchain, pAllocator);
}

static VKAPI_ATTR VkResult VKAPI_CALL
layer_QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR *pPresentInfo)
{
    DeviceData *dd = g_active_dd;
    if (!dd || !dd->dt.QueuePresentKHR)
        return VK_ERROR_DEVICE_LOST;

    ipc_poll();

    const VkSemaphore *chain_sems = pPresentInfo->pWaitSemaphores;
    uint32_t chain_sem_count = pPresentInfo->waitSemaphoreCount;

    int capture_done = 0;
    for (uint32_t i = 0; i < pPresentInfo->swapchainCount; i++) {
        if (pPresentInfo->pSwapchains[i] != dd->sc.handle)
            continue;
        uint32_t img_idx = pPresentInfo->pImageIndices[i];
        if (img_idx >= dd->sc.images.size())
            continue;
        if (dd->capture_requested && !dd->sc.no_capture &&
            !dd->capture_broken &&
            !dd->capture_pending.load(std::memory_order_acquire)) {
            capture_done = do_capture(dd, queue,
                dd->sc.images[img_idx],
                chain_sems, chain_sem_count);
            dd->capture_requested = 0;
            if (capture_done) {
                chain_sems = &dd->capture_sem;
                chain_sem_count = 1;
            }
        }
    }

    VkCommandBuffer overlay_cmd = VK_NULL_HANDLE;
    uint32_t overlay_sc_idx = 0;
    for (uint32_t i = 0; i < pPresentInfo->swapchainCount; i++) {
        if (pPresentInfo->pSwapchains[i] != dd->sc.handle)
            continue;
        overlay_sc_idx = pPresentInfo->pImageIndices[i];
        if (overlay_sc_idx >= dd->sc.images.size())
            break;
        overlay_cmd = composite_record_overlays(dd,
            dd->sc.images[overlay_sc_idx], overlay_sc_idx);
        break;
    }

    if (overlay_cmd) {
        DrawResources &dr = dd->sc.draws[overlay_sc_idx];
        auto *wait_stages = static_cast<VkPipelineStageFlags *>(
            alloca(sizeof(VkPipelineStageFlags) * chain_sem_count));
        for (uint32_t i = 0; i < chain_sem_count; i++)
            wait_stages[i] = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;

        VkSubmitInfo si{};
        si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
        si.waitSemaphoreCount = chain_sem_count;
        si.pWaitSemaphores = chain_sems;
        si.pWaitDstStageMask = wait_stages;
        si.commandBufferCount = 1;
        si.pCommandBuffers = &overlay_cmd;
        si.signalSemaphoreCount = 1;
        si.pSignalSemaphores = &dr.semaphore;

        VkResult comp_res = dd->dt.QueueSubmit(dd->gfx_queue, 1, &si, dr.fence);
        if (comp_res != VK_SUCCESS) {
            layer_log("composite QueueSubmit failed: %d", comp_res);
            dd->dt.DestroyFence(dd->device, dr.fence, nullptr);
            VkFenceCreateInfo fci_fix{};
            fci_fix.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
            fci_fix.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            dd->dt.CreateFence(dd->device, &fci_fix, nullptr, &dr.fence);
            goto passthrough_present;
        }

        VkPresentInfoKHR mod = *pPresentInfo;
        mod.waitSemaphoreCount = 1;
        mod.pWaitSemaphores = &dr.semaphore;
        return dd->dt.QueuePresentKHR(queue, &mod);
    }

passthrough_present:
    if (capture_done) {
        VkPresentInfoKHR mod = *pPresentInfo;
        mod.waitSemaphoreCount = 1;
        mod.pWaitSemaphores = &dd->capture_sem;
        return dd->dt.QueuePresentKHR(queue, &mod);
    }

    return dd->dt.QueuePresentKHR(queue, pPresentInfo);
}

/* ---- dispatch: vkGetDeviceProcAddr / vkGetInstanceProcAddr ---- */

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
layer_GetDeviceProcAddr(VkDevice device, const char *pName)
{
    if (!g_passthrough) {
        #define INTERCEPT(fn) if (strcmp(pName, "vk" #fn) == 0) \
            return reinterpret_cast<PFN_vkVoidFunction>(layer_##fn)
        INTERCEPT(DestroyDevice);
        INTERCEPT(CreateSwapchainKHR);
        INTERCEPT(DestroySwapchainKHR);
        INTERCEPT(QueuePresentKHR);
        INTERCEPT(GetDeviceProcAddr);
        #undef INTERCEPT
    }

    DeviceData *dd = get_device_data(device);
    if (dd && dd->dt.GetDeviceProcAddr)
        return dd->dt.GetDeviceProcAddr(device, pName);
    return nullptr;
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
layer_GetInstanceProcAddr(VkInstance instance, const char *pName)
{
    #define INTERCEPT(fn) if (strcmp(pName, "vk" #fn) == 0) \
        return reinterpret_cast<PFN_vkVoidFunction>(layer_##fn)
    INTERCEPT(CreateInstance);
    INTERCEPT(DestroyInstance);
    INTERCEPT(GetInstanceProcAddr);
    INTERCEPT(CreateDevice);

    if (!g_passthrough) {
        INTERCEPT(DestroyDevice);
        INTERCEPT(CreateSwapchainKHR);
        INTERCEPT(DestroySwapchainKHR);
        INTERCEPT(QueuePresentKHR);
        INTERCEPT(GetDeviceProcAddr);
    }
    #undef INTERCEPT

    if (g_passthrough && g_passthrough_gipa)
        return g_passthrough_gipa(instance, pName);

    InstanceData *inst = get_instance_data(instance);
    if (inst && inst->dt.GetInstanceProcAddr)
        return inst->dt.GetInstanceProcAddr(instance, pName);
    return nullptr;
}

/* ---- layer negotiation entry point ---- */

extern "C" VKAPI_ATTR VkResult VKAPI_CALL
vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface *pVersionStruct)
{
    fprintf(stderr, "wfinfo-vklayer: negotiate called, pid=%d\n", getpid());

    if (pVersionStruct->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT)
        return VK_ERROR_INITIALIZATION_FAILED;

    if (pVersionStruct->loaderLayerInterfaceVersion > 2)
        pVersionStruct->loaderLayerInterfaceVersion = 2;

    pVersionStruct->pfnGetInstanceProcAddr = layer_GetInstanceProcAddr;
    pVersionStruct->pfnGetDeviceProcAddr = layer_GetDeviceProcAddr;
    pVersionStruct->pfnGetPhysicalDeviceProcAddr = nullptr;

    return VK_SUCCESS;
}



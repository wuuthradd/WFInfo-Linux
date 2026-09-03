/*
 * vklayer-composite.cpp - Overlay compositing for the WFInfo Vulkan layer
 *
 * Creates a graphics pipeline that alpha-blends pre-rendered overlay
 * bitmaps (from Cairo) onto the swapchain image via a render pass.
 * Uses COLOR_ATTACHMENT_BIT (DCC safe, no GPU resets on RADV/RDNA4).
 * Handles panels, reward window (with cursor tracking), and SnapIt.
 */

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <cstdint>
#include <cmath>

#include <cairo/cairo.h>

#include "vklayer-composite.hpp"
#include "overlay.hpp"

#include "overlay-blend.vert.inc"
#include "overlay-blend.frag.inc"

#define CURSOR_FILE "/tmp/wfinfo_cursor"

static int read_cursor_file(int *out_x, int *out_y, int *out_btn,
                            unsigned int *out_seq)
{
    FILE *f = fopen(CURSOR_FILE, "r");
    if (!f) return 0;
    int n = fscanf(f, "%d %d %d %u", out_x, out_y, out_btn, out_seq);
    fclose(f);
    return n >= 3;
}

/* ---- overlay texture ---- */

struct OverlayTex {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    VkDescriptorSet ds = VK_NULL_HANDLE;
    int width = 0, height = 0;
    int valid = 0;
    int needs_transition = 0;
    VkImageLayout current_layout = VK_IMAGE_LAYOUT_UNDEFINED;
};

static void destroy_overlay_tex(DeviceData *dd, OverlayTex *tex)
{
    if (tex->ds) dd->dt.FreeDescriptorSets(dd->device, dd->blend_ds_pool, 1, &tex->ds);
    if (tex->view) dd->dt.DestroyImageView(dd->device, tex->view, nullptr);
    if (tex->image) dd->dt.DestroyImage(dd->device, tex->image, nullptr);
    if (tex->memory) dd->dt.FreeMemory(dd->device, tex->memory, nullptr);
    *tex = OverlayTex{};
}

static int ensure_overlay_tex(DeviceData *dd, OverlayTex *tex, int w, int h)
{
    if (tex->valid && tex->width == w && tex->height == h)
        return 0;

    destroy_overlay_tex(dd, tex);

    VkImageCreateInfo ici{};
    ici.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ici.imageType = VK_IMAGE_TYPE_2D;
    ici.format = VK_FORMAT_B8G8R8A8_UNORM;
    ici.extent = { static_cast<uint32_t>(w), static_cast<uint32_t>(h), 1 };
    ici.mipLevels = 1;
    ici.arrayLayers = 1;
    ici.samples = VK_SAMPLE_COUNT_1_BIT;
    ici.tiling = VK_IMAGE_TILING_LINEAR;
    ici.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    ici.initialLayout = VK_IMAGE_LAYOUT_PREINITIALIZED;
    if (dd->dt.CreateImage(dd->device, &ici, nullptr, &tex->image) != VK_SUCCESS)
        return -1;

    VkMemoryRequirements req;
    dd->dt.GetImageMemoryRequirements(dd->device, tex->image, &req);

    uint32_t mem_type = find_memory_type(&dd->mem_props, req.memoryTypeBits,
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (mem_type == UINT32_MAX) {
        dd->dt.DestroyImage(dd->device, tex->image, nullptr);
        tex->image = VK_NULL_HANDLE;
        return -1;
    }

    VkMemoryAllocateInfo mai{};
    mai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    mai.allocationSize = req.size;
    mai.memoryTypeIndex = mem_type;
    if (dd->dt.AllocateMemory(dd->device, &mai, nullptr, &tex->memory) != VK_SUCCESS) {
        dd->dt.DestroyImage(dd->device, tex->image, nullptr);
        tex->image = VK_NULL_HANDLE;
        return -1;
    }
    dd->dt.BindImageMemory(dd->device, tex->image, tex->memory, 0);

    VkImageViewCreateInfo ivci{};
    ivci.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    ivci.image = tex->image;
    ivci.viewType = VK_IMAGE_VIEW_TYPE_2D;
    ivci.format = VK_FORMAT_B8G8R8A8_UNORM;
    ivci.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    if (dd->dt.CreateImageView(dd->device, &ivci, nullptr, &tex->view) != VK_SUCCESS) {
        destroy_overlay_tex(dd, tex);
        return -1;
    }

    VkDescriptorSetAllocateInfo dsai{};
    dsai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    dsai.descriptorPool = dd->blend_ds_pool;
    dsai.descriptorSetCount = 1;
    dsai.pSetLayouts = &dd->blend_ds_layout;
    if (dd->dt.AllocateDescriptorSets(dd->device, &dsai, &tex->ds) != VK_SUCCESS) {
        destroy_overlay_tex(dd, tex);
        return -1;
    }

    tex->width = w;
    tex->height = h;
    tex->valid = 1;
    tex->needs_transition = 1;
    tex->current_layout = VK_IMAGE_LAYOUT_PREINITIALIZED;
    return 0;
}

static void upload_overlay_tex(DeviceData *dd, OverlayTex *tex,
                                const void *pixels, int w, int h)
{
    if (!tex->valid) return;

    VkSubresourceLayout layout;
    VkImageSubresource subres{};
    subres.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    dd->dt.GetImageSubresourceLayout(dd->device, tex->image, &subres, &layout);

    void *mapped = nullptr;
    if (dd->dt.MapMemory(dd->device, tex->memory, 0, layout.offset + layout.size,
                         0, &mapped) == VK_SUCCESS) {
        memset(static_cast<char *>(mapped) + layout.offset, 0, layout.size);
        int src_stride = w * 4;
        auto *src = static_cast<const char *>(pixels);
        auto *dst = static_cast<char *>(mapped) + layout.offset;
        for (int y = 0; y < h; y++)
            memcpy(dst + y * layout.rowPitch, src + y * src_stride, src_stride);
        dd->dt.UnmapMemory(dd->device, tex->memory);
        tex->needs_transition = 1;
    }
}

static void update_overlay_ds(DeviceData *dd, OverlayTex *tex, VkImageView sc_view)
{
    VkDescriptorImageInfo ov_info{};
    ov_info.sampler = dd->blend_sampler;
    ov_info.imageView = tex->view;
    ov_info.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

    VkDescriptorImageInfo input_info{};
    input_info.imageView = sc_view;
    input_info.imageLayout = VK_IMAGE_LAYOUT_GENERAL;

    VkWriteDescriptorSet writes[2]{};
    writes[0].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    writes[0].dstSet = tex->ds;
    writes[0].dstBinding = 0;
    writes[0].descriptorCount = 1;
    writes[0].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    writes[0].pImageInfo = &ov_info;
    writes[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    writes[1].dstSet = tex->ds;
    writes[1].dstBinding = 1;
    writes[1].descriptorCount = 1;
    writes[1].descriptorType = VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT;
    writes[1].pImageInfo = &input_info;
    dd->dt.UpdateDescriptorSets(dd->device, 2, writes, 0, nullptr);
}

/* ---- per-overlay textures ---- */

static std::vector<OverlayTex> panel_textures;
static std::vector<int> panel_dirty;
static OverlayTex rw_texture;
static int rw_dirty = 1;
static OverlayTex snapit_texture;
static OverlayTex rw_cursor_tex;
static int rw_cursor_ready;
static int rw_cursor_over;
static int rw_cursor_lx, rw_cursor_ly;
static OverlayTex snapit_tint_tex;
static OverlayTex snapit_sel_tex;
static OverlayTex snapit_cursor_tex;
static OverlayTex snapit_hdash_tex;
static OverlayTex snapit_vdash_tex;
int snapit_tint_ready;
int snapit_cursor_ready;
#define BORDER_W 2
#define DASH_LEN 10

void composite_mark_panel_dirty(int id) {
    if (id < 0) return;
    if (static_cast<size_t>(id) >= panel_dirty.size()) {
        panel_dirty.resize(static_cast<size_t>(id) + 1, 1);
        panel_textures.resize(static_cast<size_t>(id) + 1);
    }
    panel_dirty[id] = 1;
}
void composite_mark_rw_dirty(void) { rw_dirty = 1; }

#define PAPER_WHITE_NITS 203.0f

/* Push constant layout matching the vertex + fragment shaders (36 bytes) */
struct BlendPC {
    float ox, oy, w, h;
    float screen_w, screen_h;
    int32_t flags;
    int32_t tex_offset;
    float paper_white;
};

/* ---- pipeline setup ---- */

int composite_init_pipeline(DeviceData *dd)
{
    VkResult r;

    VkShaderModule vert_mod = VK_NULL_HANDLE;
    {
        VkShaderModuleCreateInfo smci{};
        smci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
        smci.codeSize = overlay_blend_vert_spv_len;
        smci.pCode = reinterpret_cast<const uint32_t *>(overlay_blend_vert_spv);
        r = dd->dt.CreateShaderModule(dd->device, &smci, nullptr, &vert_mod);
        if (r != VK_SUCCESS) return -1;
    }

    VkShaderModule frag_mod = VK_NULL_HANDLE;
    {
        VkShaderModuleCreateInfo smci{};
        smci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
        smci.codeSize = overlay_blend_frag_spv_len;
        smci.pCode = reinterpret_cast<const uint32_t *>(overlay_blend_frag_spv);
        r = dd->dt.CreateShaderModule(dd->device, &smci, nullptr, &frag_mod);
        if (r != VK_SUCCESS) {
            dd->dt.DestroyShaderModule(dd->device, vert_mod, nullptr);
            return -1;
        }
    }

    /* Descriptor set layout: binding 0 = overlay sampler, binding 1 = input attachment */
    VkDescriptorSetLayoutBinding bindings[2]{};
    bindings[0].binding = 0;
    bindings[0].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    bindings[0].descriptorCount = 1;
    bindings[0].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    bindings[1].binding = 1;
    bindings[1].descriptorType = VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT;
    bindings[1].descriptorCount = 1;
    bindings[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;

    VkDescriptorSetLayoutCreateInfo dslci{};
    dslci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    dslci.bindingCount = 2;
    dslci.pBindings = bindings;
    r = dd->dt.CreateDescriptorSetLayout(dd->device, &dslci, nullptr, &dd->blend_ds_layout);
    if (r != VK_SUCCESS) goto fail_shaders;

    /* Pipeline layout with push constants */
    {
        VkPushConstantRange pcr{};
        pcr.stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
        pcr.offset = 0;
        pcr.size = sizeof(BlendPC);

        VkPipelineLayoutCreateInfo plci{};
        plci.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        plci.setLayoutCount = 1;
        plci.pSetLayouts = &dd->blend_ds_layout;
        plci.pushConstantRangeCount = 1;
        plci.pPushConstantRanges = &pcr;
        r = dd->dt.CreatePipelineLayout(dd->device, &plci, nullptr, &dd->blend_layout);
        if (r != VK_SUCCESS) goto fail_shaders;
    }

    /* Render pass: swapchain is both color attachment and input attachment.
     * loadOp=LOAD preserves game content. Self-dependency allows the
     * fragment shader to read the framebuffer via subpassLoad while also
     * writing to it (needed for manual alpha blend in both SDR and HDR). */
    {
        VkAttachmentDescription att{};
        att.format = dd->sc.format;
        att.samples = VK_SAMPLE_COUNT_1_BIT;
        att.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
        att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
        att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
        att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
        att.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        att.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

        VkAttachmentReference color_ref{};
        color_ref.attachment = 0;
        color_ref.layout = VK_IMAGE_LAYOUT_GENERAL;

        VkAttachmentReference input_ref{};
        input_ref.attachment = 0;
        input_ref.layout = VK_IMAGE_LAYOUT_GENERAL;

        VkSubpassDescription subpass{};
        subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        subpass.colorAttachmentCount = 1;
        subpass.pColorAttachments = &color_ref;
        subpass.inputAttachmentCount = 1;
        subpass.pInputAttachments = &input_ref;

        VkSubpassDependency deps[2]{};
        deps[0].srcSubpass = VK_SUBPASS_EXTERNAL;
        deps[0].dstSubpass = 0;
        deps[0].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT |
                               VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        deps[0].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        deps[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT |
                                VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT |
                                VK_ACCESS_INPUT_ATTACHMENT_READ_BIT;
        /* Self-dependency: each draw reads the result of the previous draw */
        deps[1].srcSubpass = 0;
        deps[1].dstSubpass = 0;
        deps[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[1].dstStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        deps[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        deps[1].dstAccessMask = VK_ACCESS_INPUT_ATTACHMENT_READ_BIT;
        deps[1].dependencyFlags = VK_DEPENDENCY_BY_REGION_BIT;

        VkRenderPassCreateInfo rpci{};
        rpci.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
        rpci.attachmentCount = 1;
        rpci.pAttachments = &att;
        rpci.subpassCount = 1;
        rpci.pSubpasses = &subpass;
        rpci.dependencyCount = 2;
        rpci.pDependencies = deps;
        r = dd->dt.CreateRenderPass(dd->device, &rpci, nullptr, &dd->render_pass);
        if (r != VK_SUCCESS) goto fail_shaders;
    }

    /* Graphics pipeline: manual blend via subpassLoad, dynamic viewport/scissor */
    {
        VkPipelineShaderStageCreateInfo stages[2]{};
        stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
        stages[0].module = vert_mod;
        stages[0].pName = "main";
        stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        stages[1].module = frag_mod;
        stages[1].pName = "main";

        VkPipelineVertexInputStateCreateInfo vi{};
        vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;

        VkPipelineInputAssemblyStateCreateInfo ia{};
        ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
        ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;

        VkPipelineViewportStateCreateInfo vp{};
        vp.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
        vp.viewportCount = 1;
        vp.scissorCount = 1;

        VkPipelineRasterizationStateCreateInfo rs{};
        rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
        rs.polygonMode = VK_POLYGON_MODE_FILL;
        rs.cullMode = VK_CULL_MODE_NONE;
        rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
        rs.lineWidth = 1.0f;

        VkPipelineMultisampleStateCreateInfo ms{};
        ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
        ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;

        VkPipelineColorBlendAttachmentState blend_att{};
        blend_att.blendEnable = VK_FALSE;
        blend_att.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT |
                                   VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;

        VkPipelineColorBlendStateCreateInfo cb{};
        cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
        cb.attachmentCount = 1;
        cb.pAttachments = &blend_att;

        VkDynamicState dyn_states[] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
        VkPipelineDynamicStateCreateInfo dyn{};
        dyn.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
        dyn.dynamicStateCount = 2;
        dyn.pDynamicStates = dyn_states;

        VkGraphicsPipelineCreateInfo gpci{};
        gpci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
        gpci.stageCount = 2;
        gpci.pStages = stages;
        gpci.pVertexInputState = &vi;
        gpci.pInputAssemblyState = &ia;
        gpci.pViewportState = &vp;
        gpci.pRasterizationState = &rs;
        gpci.pMultisampleState = &ms;
        gpci.pColorBlendState = &cb;
        gpci.pDynamicState = &dyn;
        gpci.layout = dd->blend_layout;
        gpci.renderPass = dd->render_pass;
        gpci.subpass = 0;
        r = dd->dt.CreateGraphicsPipelines(dd->device, VK_NULL_HANDLE, 1, &gpci, nullptr,
                                            &dd->blend_pipeline);
        if (r != VK_SUCCESS) goto fail_shaders;

        blend_att.blendEnable = VK_TRUE;
        blend_att.srcColorBlendFactor = VK_BLEND_FACTOR_ONE;
        blend_att.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        blend_att.colorBlendOp = VK_BLEND_OP_ADD;
        blend_att.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
        blend_att.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        blend_att.alphaBlendOp = VK_BLEND_OP_ADD;
        r = dd->dt.CreateGraphicsPipelines(dd->device, VK_NULL_HANDLE, 1, &gpci, nullptr,
                                            &dd->blend_pipeline_hw);
        if (r != VK_SUCCESS)
            dd->blend_pipeline_hw = VK_NULL_HANDLE;
    }

    {
        VkDescriptorPoolSize pool_sizes[2]{};
        pool_sizes[0].type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        pool_sizes[0].descriptorCount = 128;
        pool_sizes[1].type = VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT;
        pool_sizes[1].descriptorCount = 128;

        VkDescriptorPoolCreateInfo dpci{};
        dpci.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
        dpci.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
        dpci.maxSets = 128;
        dpci.poolSizeCount = 2;
        dpci.pPoolSizes = pool_sizes;
        r = dd->dt.CreateDescriptorPool(dd->device, &dpci, nullptr, &dd->blend_ds_pool);
        if (r != VK_SUCCESS) goto fail_shaders;
    }

    {
        VkSamplerCreateInfo sci{};
        sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
        sci.magFilter = VK_FILTER_NEAREST;
        sci.minFilter = VK_FILTER_NEAREST;
        sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        r = dd->dt.CreateSampler(dd->device, &sci, nullptr, &dd->blend_sampler);
        if (r != VK_SUCCESS) goto fail_shaders;
    }

    dd->dt.DestroyShaderModule(dd->device, vert_mod, nullptr);
    dd->dt.DestroyShaderModule(dd->device, frag_mod, nullptr);

    fprintf(stderr, "wfinfo-vklayer: graphics pipeline ready\n");
    return 0;

fail_shaders:
    if (vert_mod) dd->dt.DestroyShaderModule(dd->device, vert_mod, nullptr);
    if (frag_mod) dd->dt.DestroyShaderModule(dd->device, frag_mod, nullptr);
    if (dd->blend_pipeline_hw) {
        dd->dt.DestroyPipeline(dd->device, dd->blend_pipeline_hw, nullptr);
        dd->blend_pipeline_hw = VK_NULL_HANDLE;
    }
    return -1;
}

void composite_cleanup(DeviceData *dd)
{
    if (!dd->device) return;

    for (auto &tex : panel_textures)
        destroy_overlay_tex(dd, &tex);
    panel_textures.clear();
    panel_dirty.clear();
    destroy_overlay_tex(dd, &rw_texture);
    destroy_overlay_tex(dd, &snapit_texture);
    destroy_overlay_tex(dd, &snapit_tint_tex);
    destroy_overlay_tex(dd, &snapit_sel_tex);
    destroy_overlay_tex(dd, &rw_cursor_tex);
    destroy_overlay_tex(dd, &snapit_cursor_tex);
    destroy_overlay_tex(dd, &snapit_hdash_tex);
    destroy_overlay_tex(dd, &snapit_vdash_tex);
    snapit_tint_ready = 0;
    snapit_cursor_ready = 0;

    for (auto &dr : dd->sc.draws) {
        if (dr.fence) dd->dt.DestroyFence(dd->device, dr.fence, nullptr);
        if (dr.semaphore) dd->dt.DestroySemaphore(dd->device, dr.semaphore, nullptr);

    }
    if (dd->composite_cmd_pool)
        dd->dt.DestroyCommandPool(dd->device, dd->composite_cmd_pool, nullptr);
    dd->composite_cmd_pool = VK_NULL_HANDLE;

    for (auto &fb : dd->sc.framebuffers)
        if (fb) dd->dt.DestroyFramebuffer(dd->device, fb, nullptr);

    if (dd->blend_sampler) dd->dt.DestroySampler(dd->device, dd->blend_sampler, nullptr);
    if (dd->blend_ds_pool) dd->dt.DestroyDescriptorPool(dd->device, dd->blend_ds_pool, nullptr);
    if (dd->blend_pipeline) dd->dt.DestroyPipeline(dd->device, dd->blend_pipeline, nullptr);
    if (dd->blend_pipeline_hw) dd->dt.DestroyPipeline(dd->device, dd->blend_pipeline_hw, nullptr);
    dd->blend_pipeline = VK_NULL_HANDLE;
    dd->blend_pipeline_hw = VK_NULL_HANDLE;
    if (dd->blend_layout) dd->dt.DestroyPipelineLayout(dd->device, dd->blend_layout, nullptr);
    if (dd->blend_ds_layout) dd->dt.DestroyDescriptorSetLayout(dd->device, dd->blend_ds_layout, nullptr);
    if (dd->render_pass) dd->dt.DestroyRenderPass(dd->device, dd->render_pass, nullptr);

}

/* ---- draw helpers ---- */

/* Emit a color-write to input-read barrier between overlapping draw calls.
 * Without this, subpassLoad reads stale framebuffer data from before the
 * previous draw wrote its blended result, causing black/static artifacts. */
static inline void emit_draw_barrier(DeviceData *dd, VkCommandBuffer cmd)
{
    VkMemoryBarrier b{};
    b.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    b.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    b.dstAccessMask = VK_ACCESS_INPUT_ATTACHMENT_READ_BIT;
    dd->dt.CmdPipelineBarrier(cmd,
        VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
        VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
        VK_DEPENDENCY_BY_REGION_BIT,
        1, &b, 0, nullptr, 0, nullptr);
}

static int blend_hdr_flag(DeviceData *dd)
{
    return (dd->sc.colorspace == VK_COLOR_SPACE_HDR10_ST2084_EXT) ? 2 : 0;
}

static void draw_overlay(DeviceData *dd, VkCommandBuffer cmd, VkImageView sc_view,
                          OverlayTex *tex, int ox, int oy, int w, int h,
                          int flags_extra, int tex_off)
{
    if (!tex->valid || w <= 0 || h <= 0) return;

    update_overlay_ds(dd, tex, sc_view);

    dd->dt.CmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS,
        dd->blend_layout, 0, 1, &tex->ds, 0, nullptr);

    BlendPC pc{};
    pc.ox = static_cast<float>(ox);
    pc.oy = static_cast<float>(oy);
    pc.w = static_cast<float>(w);
    pc.h = static_cast<float>(h);
    pc.screen_w = static_cast<float>(dd->sc.width);
    pc.screen_h = static_cast<float>(dd->sc.height);
    pc.flags = blend_hdr_flag(dd) | flags_extra;
    pc.tex_offset = tex_off;
    pc.paper_white = PAPER_WHITE_NITS;

    dd->dt.CmdPushConstants(cmd, dd->blend_layout,
        VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT,
        0, sizeof(pc), &pc);

    VkViewport vp{};
    vp.x = 0.0f;
    vp.y = 0.0f;
    vp.width = static_cast<float>(dd->sc.width);
    vp.height = static_cast<float>(dd->sc.height);
    vp.minDepth = 0.0f;
    vp.maxDepth = 1.0f;
    dd->dt.CmdSetViewport(cmd, 0, 1, &vp);

    VkRect2D scissor{};
    scissor.offset = { ox < 0 ? 0 : ox, oy < 0 ? 0 : oy };
    scissor.extent = { static_cast<uint32_t>(w), static_cast<uint32_t>(h) };
    dd->dt.CmdSetScissor(cmd, 0, 1, &scissor);

    dd->dt.CmdDraw(cmd, 6, 1, 0, 0);
}

/* ---- reward window cursor tracking ---- */

static unsigned int rw_cursor_seq;
static int rw_btn_prev;

static void rw_cursor_tick(int rx, int ry, int btn, unsigned int seq)
{
    if (!rw.visible) return;
    if (seq == rw_cursor_seq) return;
    rw_cursor_seq = seq;
    int btn_now = (btn & 1) != 0;

    int over = (rx >= rw.offset_x && rx < rw.offset_x + rw.total_w &&
                ry >= rw.offset_y && ry < rw.offset_y + RW_TOTAL_H);

    if (btn_now && !rw_btn_prev && over && !rw.dragging) {
        double cx = rx - rw.offset_x;
        double cy = ry - rw.offset_y;
        if (cx >= rw.total_w - 30 && cy >= 0 && cy <= RW_TITLE_H) {
            rw.visible = 0;
            rw.configured = 0;
            rw.dragging = 0;
            rw_btn_prev = btn_now;
            return;
        }
        rw.dragging = 1;
        rw.drag_start_px = rx;
        rw.drag_start_py = ry;
        rw.drag_start_ox = rw.offset_x;
        rw.drag_start_oy = rw.offset_y;
    } else if (!btn_now && rw_btn_prev && rw.dragging) {
        rw.dragging = 0;
    }
    rw_btn_prev = btn_now;

    if (rw.dragging) {
        rw.offset_x = rw.drag_start_ox + (rx - static_cast<int>(rw.drag_start_px));
        rw.offset_y = rw.drag_start_oy + (ry - static_cast<int>(rw.drag_start_py));
    }

    rw_cursor_over = over || rw.dragging;
    rw_cursor_lx = rx - rw.offset_x;
    rw_cursor_ly = ry - rw.offset_y;
}

/* ---- snapit cursor tracking ---- */

int snap_btn_prev;

static void snapit_cursor_tick(int rx, int ry, int btn)
{
    if (!snapit.active) return;
    int btn_now = (btn & 1) != 0;

    snapit.cur_x = rx;
    snapit.cur_y = ry;

    if (snap_btn_prev < 0) {
        snap_btn_prev = btn_now;
        return;
    }

    if (btn_now && !snap_btn_prev && !snapit.dragging) {
        handle_snapit_press(rx, ry);
    } else if (btn_now && snapit.dragging) {
        handle_snapit_motion(rx, ry);
        snapit.dash_offset += 0.15;
        if (snapit.dash_offset >= static_cast<double>(DASH_LEN)) snapit.dash_offset = 0.0;
    } else if (!btn_now && snap_btn_prev && snapit.dragging) {
        layer_log("snapit release: start=(%d,%d) cur=(%d,%d)",
                  static_cast<int>(snapit.start_x), static_cast<int>(snapit.start_y),
                  static_cast<int>(snapit.cur_x), static_cast<int>(snapit.cur_y));
        handle_snapit_release(rx, ry);
    }
    snap_btn_prev = btn_now;
}

/* ---- software cursor rendering ---- */

static void draw_sw_cursor(cairo_t *cr, double x, double y)
{
    cairo_save(cr);
    cairo_translate(cr, x, y);
    cairo_new_path(cr);
    cairo_move_to(cr, 0, 0);
    cairo_line_to(cr, 0, 17);
    cairo_line_to(cr, 4, 12);
    cairo_line_to(cr, 8, 20);
    cairo_line_to(cr, 11, 18);
    cairo_line_to(cr, 7, 11);
    cairo_line_to(cr, 12, 11);
    cairo_close_path(cr);
    cairo_set_source_rgb(cr, 1, 1, 1);
    cairo_fill_preserve(cr);
    cairo_set_source_rgb(cr, 0, 0, 0);
    cairo_set_line_width(cr, 1.2);
    cairo_stroke(cr);
    cairo_restore(cr);
}

/* ---- per-frame overlay composite ---- */

VkCommandBuffer composite_record_overlays(DeviceData *dd,
                                           VkImage /*sc_image*/, uint32_t sc_idx)
{
    if (!dd->blend_pipeline || dd->sc.images.empty())
        return VK_NULL_HANDLE;

    int any_visible = 0;
    for (size_t i = 0; i < panels.size(); i++) {
        if (panels[i].visible) { any_visible = 1; break; }
    }
    if (rw.visible) any_visible = 1;
    if (snapit.active) any_visible = 1;

    if (!any_visible) return VK_NULL_HANDLE;

    int cx, cy, cbtn;
    unsigned int cseq = 0;
    if (read_cursor_file(&cx, &cy, &cbtn, &cseq)) {
        rw_cursor_tick(cx, cy, cbtn, cseq);
        snapit_cursor_tick(cx, cy, cbtn);
    }

    time_t now = time(nullptr);
    for (size_t i = 0; i < panels.size(); i++) {
        if (panels[i].visible && panels[i].hide_at > 0 && now >= panels[i].hide_at)
            hide_panel_by_id(static_cast<int>(i));
    }

    if (sc_idx >= dd->sc.framebuffers.size() || !dd->sc.framebuffers[sc_idx])
        return VK_NULL_HANDLE;

    /* Nothing visible, skip the entire render pass + submit */
    int has_visible_panel = 0;
    for (size_t i = 0; i < panels.size(); i++) {
        if (panels[i].visible) { has_visible_panel = 1; break; }
    }
    if (!has_visible_panel && !rw.visible && !snapit.active)
        return VK_NULL_HANDLE;

    /* Ensure per-frame command pool and draw resources */
    if (!dd->composite_cmd_pool) {
        VkCommandPoolCreateInfo cpci{};
        cpci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
        cpci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        cpci.queueFamilyIndex = dd->gfx_queue_family;
        if (dd->dt.CreateCommandPool(dd->device, &cpci, nullptr, &dd->composite_cmd_pool) != VK_SUCCESS)
            return VK_NULL_HANDLE;

        uint32_t count = static_cast<uint32_t>(dd->sc.draws.size());
        std::vector<VkCommandBuffer> cmds(count, VK_NULL_HANDLE);
        VkCommandBufferAllocateInfo cbai{};
        cbai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        cbai.commandPool = dd->composite_cmd_pool;
        cbai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        cbai.commandBufferCount = count;
        if (dd->dt.AllocateCommandBuffers(dd->device, &cbai, cmds.data()) != VK_SUCCESS)
            return VK_NULL_HANDLE;

        VkSemaphoreCreateInfo sci{};
        sci.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        VkFenceCreateInfo fci{};
        fci.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
        fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        for (uint32_t i = 0; i < count; i++) {
            dd->sc.draws[i].cmd = cmds[i];
            dd->dt.CreateSemaphore(dd->device, &sci, nullptr, &dd->sc.draws[i].semaphore);
            dd->dt.CreateFence(dd->device, &fci, nullptr, &dd->sc.draws[i].fence);
        }
    }

    DrawResources &dr = dd->sc.draws[sc_idx];
    if (!dr.cmd) return VK_NULL_HANDLE;

    /* Wait for this image's previous overlay submission to finish */
    VkResult fence_res = dd->dt.WaitForFences(dd->device, 1,
        &dr.fence, VK_TRUE, 1000000000ULL);
    if (fence_res == VK_ERROR_DEVICE_LOST) {
        dd->device_lost.store(1, std::memory_order_release);
        layer_log("DEVICE_LOST during composite fence wait (image %u)", sc_idx);
        uint64_t cs = dd->composite_submits.load(std::memory_order_acquire);
        uint64_t cc = dd->composite_completes.load(std::memory_order_acquire);
        uint64_t xs = dd->capture_submits.load(std::memory_order_acquire);
        uint64_t xc = dd->capture_completes.load(std::memory_order_acquire);
        layer_log("DEVICE_LOST composite: %lu/%lu in-flight, capture: %lu/%lu in-flight",
                  cs - cc, cs, xs - xc, xs);
        layer_log_sync();
        return VK_NULL_HANDLE;
    }
    if (fence_res == VK_TIMEOUT) {
        layer_log("composite fence wait timed out, skipping overlay this frame");
        return VK_NULL_HANDLE;
    }
    if (fence_res == VK_SUCCESS)
        dd->composite_completes.fetch_add(1, std::memory_order_release);
    dd->dt.ResetFences(dd->device, 1, &dr.fence);

    /* Render + upload dirty panel textures */
    if (panel_textures.size() < panels.size()) {
        panel_textures.resize(panels.size());
        panel_dirty.resize(panels.size(), 1);
    }
    for (size_t i = 0; i < panels.size(); i++) {
        Panel *p = &panels[i];
        if (!p->visible || p->w <= 0 || p->h <= 0) continue;
        if (panel_dirty[i] || !panel_textures[i].valid) {
            if (ensure_overlay_tex(dd, &panel_textures[i], p->w, p->h) == 0) {
                size_t sz = static_cast<size_t>(p->w) * p->h * 4;
                void *buf = calloc(1, sz);
                if (buf) {
                    render_panel(p, buf, 1);
                    upload_overlay_tex(dd, &panel_textures[i], buf, p->w, p->h);
                    free(buf);
                }
            }
            panel_dirty[i] = 0;
        }
    }

    /* Render + upload reward window texture */
    if (rw.visible && rw.total_w > 0) {
        int rw_w = rw.total_w, rw_h = RW_TOTAL_H;
        if (rw_dirty || !rw_texture.valid) {
            if (ensure_overlay_tex(dd, &rw_texture, rw_w, rw_h) == 0) {
                size_t sz = static_cast<size_t>(rw_w) * rw_h * 4;
                void *buf = calloc(1, sz);
                if (buf) {
                    cairo_surface_t *cs = cairo_image_surface_create_for_data(
                        static_cast<unsigned char *>(buf),
                        CAIRO_FORMAT_ARGB32, rw_w, rw_h, rw_w * 4);
                    cairo_t *cr = cairo_create(cs);
                    cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
                    cairo_set_source_rgba(cr, 0.106, 0.106, 0.106, 1.0);
                    cairo_paint(cr);
                    cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
                    render_rw_content(cr);
                    cairo_destroy(cr);
                    cairo_surface_destroy(cs);
                    upload_overlay_tex(dd, &rw_texture, buf, rw_w, rw_h);
                    free(buf);
                }
            }
            rw_dirty = 0;
        }
    }

    /* RW arrow cursor */
    if (rw.visible && !rw_cursor_ready) {
        constexpr int RW_CUR_SZ = 24;
        if (ensure_overlay_tex(dd, &rw_cursor_tex, RW_CUR_SZ, RW_CUR_SZ) == 0) {
            size_t csz = RW_CUR_SZ * RW_CUR_SZ * 4;
            void *cbuf = calloc(1, csz);
            if (cbuf) {
                cairo_surface_t *ccs = cairo_image_surface_create_for_data(
                    static_cast<unsigned char *>(cbuf),
                    CAIRO_FORMAT_ARGB32, RW_CUR_SZ, RW_CUR_SZ, RW_CUR_SZ * 4);
                cairo_t *ccr = cairo_create(ccs);
                cairo_set_operator(ccr, CAIRO_OPERATOR_SOURCE);
                cairo_set_source_rgba(ccr, 0, 0, 0, 0);
                cairo_paint(ccr);
                cairo_set_operator(ccr, CAIRO_OPERATOR_OVER);
                draw_sw_cursor(ccr, 1, 1);
                cairo_destroy(ccr);
                cairo_surface_destroy(ccs);
                upload_overlay_tex(dd, &rw_cursor_tex, cbuf, RW_CUR_SZ, RW_CUR_SZ);
                free(cbuf);
            }
        }
        rw_cursor_ready = 1;
    }

    /* SnapIt: tint, selection tiles, crosshair, hint */
    if (snapit.active && dd->sc.width > 0 && dd->sc.height > 0) {
        snapit.surf_w = static_cast<int>(dd->sc.width);
        snapit.surf_h = static_cast<int>(dd->sc.height);
        if (!snapit_tint_ready) {
            if (ensure_overlay_tex(dd, &snapit_tint_tex, 1, 1) == 0) {
                uint8_t tint[4] = { 16, 16, 16, 16 };
                upload_overlay_tex(dd, &snapit_tint_tex, tint, 1, 1);
            }
            if (ensure_overlay_tex(dd, &snapit_sel_tex, 1, 1) == 0) {
                uint8_t sel[4] = { 51, 51, 51, 51 };
                upload_overlay_tex(dd, &snapit_sel_tex, sel, 1, 1);
            }
            if (ensure_overlay_tex(dd, &snapit_hdash_tex, DASH_LEN, BORDER_W) == 0) {
                uint8_t hd[DASH_LEN * BORDER_W * 4];
                memset(hd, 0, sizeof(hd));
                for (int row = 0; row < BORDER_W; row++)
                    for (int col = 0; col < DASH_LEN / 2; col++) {
                        int idx = (row * DASH_LEN + col) * 4;
                        hd[idx] = hd[idx+1] = hd[idx+2] = hd[idx+3] = 255;
                    }
                upload_overlay_tex(dd, &snapit_hdash_tex, hd, DASH_LEN, BORDER_W);
            }
            if (ensure_overlay_tex(dd, &snapit_vdash_tex, BORDER_W, DASH_LEN) == 0) {
                uint8_t vd[BORDER_W * DASH_LEN * 4];
                memset(vd, 0, sizeof(vd));
                for (int row = 0; row < DASH_LEN / 2; row++)
                    for (int col = 0; col < BORDER_W; col++) {
                        int idx = (row * BORDER_W + col) * 4;
                        vd[idx] = vd[idx+1] = vd[idx+2] = vd[idx+3] = 255;
                    }
                upload_overlay_tex(dd, &snapit_vdash_tex, vd, BORDER_W, DASH_LEN);
            }
            if (snapit.hint_cs) {
                int hw = snapit.hint_w, hh = snapit.hint_h;
                if (hw > 0 && hh > 0 && ensure_overlay_tex(dd, &snapit_texture, hw, hh) == 0) {
                    size_t sz = static_cast<size_t>(hw) * hh * 4;
                    void *buf = calloc(1, sz);
                    if (buf) {
                        cairo_surface_t *cs = cairo_image_surface_create_for_data(
                            static_cast<unsigned char *>(buf),
                            CAIRO_FORMAT_ARGB32, hw, hh, hw * 4);
                        cairo_t *cr = cairo_create(cs);
                        cairo_set_source_surface(cr, snapit.hint_cs, 0, 0);
                        cairo_paint(cr);
                        cairo_destroy(cr);
                        cairo_surface_destroy(cs);
                        upload_overlay_tex(dd, &snapit_texture, buf, hw, hh);
                        free(buf);
                    }
                }
            }
            snapit_tint_ready = 1;
        }

        if (!snapit_cursor_ready) {
            constexpr int CURSOR_SZ = 32;
            if (ensure_overlay_tex(dd, &snapit_cursor_tex, CURSOR_SZ, CURSOR_SZ) == 0) {
                size_t csz = CURSOR_SZ * CURSOR_SZ * 4;
                void *cbuf = calloc(1, csz);
                if (cbuf) {
                    cairo_surface_t *ccs = cairo_image_surface_create_for_data(
                        static_cast<unsigned char *>(cbuf),
                        CAIRO_FORMAT_ARGB32, CURSOR_SZ, CURSOR_SZ, CURSOR_SZ * 4);
                    cairo_t *ccr = cairo_create(ccs);
                    cairo_set_operator(ccr, CAIRO_OPERATOR_SOURCE);
                    cairo_set_source_rgba(ccr, 0, 0, 0, 0);
                    cairo_paint(ccr);
                    cairo_set_operator(ccr, CAIRO_OPERATOR_OVER);
                    double ctr = CURSOR_SZ / 2.0, cty = CURSOR_SZ / 2.0;
                    double gap = 4.0, arm = 12.0;
                    cairo_set_line_width(ccr, 3.0);
                    cairo_set_source_rgba(ccr, 0, 0, 0, 0.9);
                    cairo_move_to(ccr, ctr - gap - arm, cty);
                    cairo_line_to(ccr, ctr - gap, cty);
                    cairo_move_to(ccr, ctr + gap, cty);
                    cairo_line_to(ccr, ctr + gap + arm, cty);
                    cairo_move_to(ccr, ctr, cty - gap - arm);
                    cairo_line_to(ccr, ctr, cty - gap);
                    cairo_move_to(ccr, ctr, cty + gap);
                    cairo_line_to(ccr, ctr, cty + gap + arm);
                    cairo_stroke(ccr);
                    cairo_set_line_width(ccr, 1.2);
                    cairo_set_source_rgba(ccr, 1, 1, 1, 1);
                    cairo_move_to(ccr, ctr - gap - arm, cty);
                    cairo_line_to(ccr, ctr - gap, cty);
                    cairo_move_to(ccr, ctr + gap, cty);
                    cairo_line_to(ccr, ctr + gap + arm, cty);
                    cairo_move_to(ccr, ctr, cty - gap - arm);
                    cairo_line_to(ccr, ctr, cty - gap);
                    cairo_move_to(ccr, ctr, cty + gap);
                    cairo_line_to(ccr, ctr, cty + gap + arm);
                    cairo_stroke(ccr);
                    cairo_destroy(ccr);
                    cairo_surface_destroy(ccs);
                    upload_overlay_tex(dd, &snapit_cursor_tex, cbuf, CURSOR_SZ, CURSOR_SZ);
                    free(cbuf);
                }
            }
            snapit_cursor_ready = 1;
        }
    }

    VkCommandBuffer cmd = dr.cmd;
    VkCommandBufferBeginInfo cbbi{};
    cbbi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    cbbi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    dd->dt.ResetCommandBuffer(cmd, 0);
    dd->dt.BeginCommandBuffer(cmd, &cbbi);

    /* Transition overlay textures to SHADER_READ_ONLY_OPTIMAL */
    {
        std::vector<VkImageMemoryBarrier> ov_barriers;
        ov_barriers.reserve(panels.size() + 10);
        auto push_barrier = [&](OverlayTex *t) {
            if (t->valid && t->needs_transition) {
                VkImageMemoryBarrier b{};
                b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
                b.srcAccessMask = VK_ACCESS_HOST_WRITE_BIT;
                b.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
                b.oldLayout = t->current_layout;
                b.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
                b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
                b.image = t->image;
                b.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
                ov_barriers.push_back(b);
                t->current_layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
                t->needs_transition = 0;
            }
        };
        for (size_t i = 0; i < panel_textures.size(); i++)
            push_barrier(&panel_textures[i]);
        push_barrier(&rw_texture);
        push_barrier(&rw_cursor_tex);
        push_barrier(&snapit_texture);
        push_barrier(&snapit_tint_tex);
        push_barrier(&snapit_sel_tex);
        push_barrier(&snapit_hdash_tex);
        push_barrier(&snapit_vdash_tex);
        push_barrier(&snapit_cursor_tex);

        if (!ov_barriers.empty()) {
            dd->dt.CmdPipelineBarrier(cmd,
                VK_PIPELINE_STAGE_HOST_BIT,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                0, 0, nullptr, 0, nullptr,
                static_cast<uint32_t>(ov_barriers.size()), ov_barriers.data());
        }
    }

    /* Begin render pass (loadOp=LOAD preserves game frame, layout transitions automatic) */
    VkRenderPassBeginInfo rpbi{};
    rpbi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rpbi.renderPass = dd->render_pass;
    rpbi.framebuffer = dd->sc.framebuffers[sc_idx];
    rpbi.renderArea = { {0, 0}, {dd->sc.width, dd->sc.height} };
    dd->dt.CmdBeginRenderPass(cmd, &rpbi, VK_SUBPASS_CONTENTS_INLINE);

    dd->dt.CmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, dd->blend_pipeline);

    VkImageView sc_view = dd->sc.image_views[sc_idx];

    int any_drawn = 0;

    /* Draw panels (barrier between each for overlapping cards) */
    for (size_t i = 0; i < panels.size(); i++) {
        Panel *p = &panels[i];
        if (!p->visible || i >= panel_textures.size() || !panel_textures[i].valid) continue;
        if (any_drawn)
            emit_draw_barrier(dd, cmd);
        draw_overlay(dd, cmd, sc_view, &panel_textures[i], p->x, p->y, p->w, p->h, 0, 0);
        any_drawn = 1;
    }

    /* Draw reward window + cursor */
    if (rw.visible && rw_texture.valid) {
        if (any_drawn)
            emit_draw_barrier(dd, cmd);
        draw_overlay(dd, cmd, sc_view, &rw_texture,
                     rw.offset_x, rw.offset_y, rw.total_w, RW_TOTAL_H, 0, 0);
        any_drawn = 1;
        if (rw_cursor_over && rw_cursor_tex.valid) {
            emit_draw_barrier(dd, cmd);
            draw_overlay(dd, cmd, sc_view, &rw_cursor_tex,
                         rw.offset_x + rw_cursor_lx,
                         rw.offset_y + rw_cursor_ly,
                         rw_cursor_tex.width, rw_cursor_tex.height, 0, 0);
        }
    }

    /* Draw snapit overlay: tint, selection rect, crosshair, hint.
     * Each layer reads the framebuffer modified by the previous one,
     * so we need a barrier between overlapping draws. */
    if (snapit.active && snapit_tint_tex.valid) {
        if (any_drawn)
            emit_draw_barrier(dd, cmd);
        /* Fullscreen tint. Hardware blend so a broken subpassLoad cannot
         * turn the light veil into an opaque white sheet. */
        if (!blend_hdr_flag(dd) && dd->blend_pipeline_hw) {
            dd->dt.CmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, dd->blend_pipeline_hw);
            draw_overlay(dd, cmd, sc_view, &snapit_tint_tex,
                         0, 0, static_cast<int>(dd->sc.width), static_cast<int>(dd->sc.height), 4, 0);
            dd->dt.CmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, dd->blend_pipeline);
        } else {
            draw_overlay(dd, cmd, sc_view, &snapit_tint_tex,
                         0, 0, static_cast<int>(dd->sc.width), static_cast<int>(dd->sc.height), 0, 0);
        }

        /* Selection rectangle: fill + marching ants border */
        if (snapit.dragging && snapit_sel_tex.valid) {
            int sx = static_cast<int>(snapit.start_x < snapit.cur_x ? snapit.start_x : snapit.cur_x);
            int sy = static_cast<int>(snapit.start_y < snapit.cur_y ? snapit.start_y : snapit.cur_y);
            int sw = static_cast<int>(snapit.cur_x - snapit.start_x);
            int sh = static_cast<int>(snapit.cur_y - snapit.start_y);
            if (sw < 0) sw = -sw;
            if (sh < 0) sh = -sh;
            if (sw > 1 && sh > 1) {
                emit_draw_barrier(dd, cmd);
                draw_overlay(dd, cmd, sc_view, &snapit_sel_tex, sx, sy, sw, sh, 0, 0);
                int doff = static_cast<int>(snapit.dash_offset);
                emit_draw_barrier(dd, cmd);
                if (snapit_hdash_tex.valid) {
                    draw_overlay(dd, cmd, sc_view, &snapit_hdash_tex,
                                 sx, sy, sw, BORDER_W, 1, doff);
                    draw_overlay(dd, cmd, sc_view, &snapit_hdash_tex,
                                 sx, sy + sh - BORDER_W, sw, BORDER_W, 1, doff);
                }
                if (snapit_vdash_tex.valid) {
                    draw_overlay(dd, cmd, sc_view, &snapit_vdash_tex,
                                 sx, sy, BORDER_W, sh, 1, doff);
                    draw_overlay(dd, cmd, sc_view, &snapit_vdash_tex,
                                 sx + sw - BORDER_W, sy, BORDER_W, sh, 1, doff);
                }
            }
        }

        /* Crosshair cursor at current mouse position */
        if (snapit_cursor_tex.valid) {
            emit_draw_barrier(dd, cmd);
            int mx = static_cast<int>(snapit.cur_x) - snapit_cursor_tex.width / 2;
            int my = static_cast<int>(snapit.cur_y) - snapit_cursor_tex.height / 2;
            draw_overlay(dd, cmd, sc_view, &snapit_cursor_tex,
                         mx, my, snapit_cursor_tex.width, snapit_cursor_tex.height, 0, 0);
        }

        /* Hint text at bottom center */
        if (snapit_texture.valid) {
            emit_draw_barrier(dd, cmd);
            int hx = (static_cast<int>(dd->sc.width) - snapit_texture.width) / 2;
            int hy = static_cast<int>(dd->sc.height) - snapit_texture.height - 10;
            draw_overlay(dd, cmd, sc_view, &snapit_texture,
                         hx, hy, snapit_texture.width, snapit_texture.height, 0, 0);
        }
    }

    dd->dt.CmdEndRenderPass(cmd);
    dd->dt.EndCommandBuffer(cmd);
    return cmd;
}
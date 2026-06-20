#version 450

/* Overlay quad vertex shader.
 * Generates a quad from gl_VertexIndex (0-5), no vertex buffer needed.
 * Push constants define screen-space position and size. */

layout(push_constant) uniform PC {
    vec2 offset;       /* top-left corner in pixels */
    vec2 size;         /* quad width x height in pixels */
    vec2 screen;       /* swapchain dimensions in pixels */
    int flags;         /* bit 0: tile mode, bit 1: HDR PQ */
    int tex_offset;    /* texel offset for tiled animation (marching ants) */
    float paper_white; /* SDR reference white in nits (HDR mode only) */
};

layout(location = 0) out vec2 vUV;

void main()
{
    /* Two-triangle quad from vertex index:
     *   idx 0 -> (0,0)  idx 1 -> (1,0)  idx 2 -> (0,1)
     *   idx 3 -> (1,0)  idx 4 -> (1,1)  idx 5 -> (0,1) */
    vec2 pos;
    switch (gl_VertexIndex) {
        case 0: pos = vec2(0.0, 0.0); break;
        case 1: pos = vec2(1.0, 0.0); break;
        case 2: pos = vec2(0.0, 1.0); break;
        case 3: pos = vec2(1.0, 0.0); break;
        case 4: pos = vec2(1.0, 1.0); break;
        default: pos = vec2(0.0, 1.0); break;
    }

    vUV = pos;

    /* Transform unit quad to screen-space pixel position, then to NDC */
    vec2 pixel = offset + pos * size;
    vec2 ndc = pixel / screen * 2.0 - 1.0;
    gl_Position = vec4(ndc, 0.0, 1.0);
}

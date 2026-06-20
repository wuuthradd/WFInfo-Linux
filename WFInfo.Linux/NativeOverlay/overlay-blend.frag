#version 450

/* Overlay blend fragment shader.
 *
 * SDR: manual premultiplied alpha blend via input attachment readback.
 * HDR PQ: manual blend in sRGB space via input attachment readback.
 * Tile mode (flags bit 0): texture wraps with animated offset.
 * flags bit 1: HDR PQ path. */

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D overlay_tex;

/* Input attachment for framebuffer readback (HDR PQ blending) */
layout(input_attachment_index = 0, set = 0, binding = 1) uniform subpassInput background;

layout(push_constant) uniform PC {
    vec2 offset;
    vec2 size;
    vec2 screen;
    int flags;
    int tex_offset;
    float paper_white;
};

/* ---- PQ / sRGB / gamut conversion ---- */

const float pq_m1 = 0.1593017578125;
const float pq_m2 = 78.84375;
const float pq_c1 = 0.8359375;
const float pq_c2 = 18.8515625;
const float pq_c3 = 18.6875;

float pq_oetf(float Y)
{
    float Ym1 = pow(Y, pq_m1);
    return pow((pq_c1 + pq_c2 * Ym1) / (1.0 + pq_c3 * Ym1), pq_m2);
}
vec3 pq_oetf(vec3 v) { return vec3(pq_oetf(v.r), pq_oetf(v.g), pq_oetf(v.b)); }

float pq_eotf(float N)
{
    float Np = pow(N, 1.0 / pq_m2);
    float num = max(Np - pq_c1, 0.0);
    float den = max(pq_c2 - pq_c3 * Np, 1e-12);
    return pow(num / den, 1.0 / pq_m1);
}
vec3 pq_eotf(vec3 v) { return vec3(pq_eotf(v.r), pq_eotf(v.g), pq_eotf(v.b)); }

float srgb_eotf(float v)
{
    if (v <= 0.04045)
        return v / 12.92;
    return pow((v + 0.055) / 1.055, 2.4);
}
vec3 srgb_eotf(vec3 v) { return vec3(srgb_eotf(v.r), srgb_eotf(v.g), srgb_eotf(v.b)); }

float srgb_oetf(float v)
{
    if (v <= 0.0031308)
        return v * 12.92;
    return 1.055 * pow(v, 1.0 / 2.4) - 0.055;
}
vec3 srgb_oetf(vec3 v) { return vec3(srgb_oetf(v.r), srgb_oetf(v.g), srgb_oetf(v.b)); }

vec3 bt2020_to_bt709(vec3 c)
{
    return vec3(
         1.6605 * c.r - 0.5876 * c.g - 0.0728 * c.b,
        -0.1246 * c.r + 1.1329 * c.g - 0.0083 * c.b,
        -0.0182 * c.r - 0.1006 * c.g + 1.1187 * c.b
    );
}

vec3 bt709_to_bt2020(vec3 c)
{
    return vec3(
        0.6274 * c.r + 0.3293 * c.g + 0.0433 * c.b,
        0.0691 * c.r + 0.9195 * c.g + 0.0114 * c.b,
        0.0164 * c.r + 0.0880 * c.g + 0.8956 * c.b
    );
}

void main()
{
    ivec2 tex_size = textureSize(overlay_tex, 0);
    vec2 tc;

    if ((flags & 1) != 0) {
        /* Tile mode: wrap with animation offset */
        ivec2 px = ivec2(vUV * size);
        tc = (vec2((px + tex_offset) % tex_size) + 0.5) / vec2(tex_size);
    } else {
        tc = vUV;
    }

    vec4 fg = texture(overlay_tex, tc);

    if ((flags & 2) != 0) {
        /* HDR PQ mode: manual blend in sRGB space */
        float a = fg.a;
        if (a < 0.001) {
            outColor = subpassLoad(background);
            return;
        }

        vec4 bg = subpassLoad(background);

        /* Background: PQ -> linear BT.2020 (normalized to 10000 nits) */
        vec3 bg_lin2020 = pq_eotf(bg.rgb);

        /* Rescale so paper_white maps to 1.0, then gamut convert to BT.709 */
        vec3 bg_lin709 = bt2020_to_bt709(bg_lin2020 * (10000.0 / paper_white));

        /* Linear BT.709 -> sRGB */
        vec3 bg_srgb = srgb_oetf(clamp(bg_lin709, 0.0, 1.0));

        /* Premultiplied alpha blend in sRGB, identical to SDR path */
        vec3 blended_srgb = fg.rgb + bg_srgb * (1.0 - a);

        /* Encode: sRGB -> linear BT.709 */
        vec3 res_lin709 = srgb_eotf(blended_srgb);

        /* Linear BT.709 -> linear BT.2020, rescale to PQ normalization */
        vec3 res_lin2020 = bt709_to_bt2020(res_lin709) * (paper_white / 10000.0);

        /* Linear BT.2020 -> PQ */
        vec3 result = pq_oetf(res_lin2020);
        outColor = vec4(result, bg.a);
    } else {
        /* SDR: manual premultiplied alpha blend via input attachment */
        float a = fg.a;
        if (a < 0.001) {
            outColor = subpassLoad(background);
            return;
        }
        vec4 bg = subpassLoad(background);
        outColor = vec4(fg.rgb + bg.rgb * (1.0 - a), bg.a);
    }
}

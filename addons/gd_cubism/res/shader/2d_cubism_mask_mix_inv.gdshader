// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2023 MizunagiKB <mizukb@live.jp>
// GDCubism shader: Mask + MixInv
shader_type canvas_item;
uniform sampler2D position_texture : filter_nearest, repeat_disable;
uniform bool use_position_texture = false;
uniform sampler2D parameter_texture : filter_nearest, repeat_disable;
render_mode blend_premul_alpha, unshaded;

varying vec4 color_base;
varying vec4 color_screen;
varying vec4 color_multiply;
uniform vec4 channel : source_color;
uniform sampler2D tex_main : filter_linear_mipmap;
uniform sampler2D tex_mask : filter_linear_mipmap;

varying float mask_scale;
uniform vec2 canvas_size;
varying vec2 mesh_offset;
varying vec4 atlas_rect;

varying vec2 MASK_UV;
varying vec4 modulate;

vec4 parameter_at(int index) {
    return texelFetch(parameter_texture, ivec2(index % 1024, index / 1024), 0);
}

void vertex() {
    int position_id = int(round(VERTEX.x));
    int parameter_id = int(texelFetch(position_texture, ivec2(position_id % 1024, position_id / 1024), 0).z) * 5;
    color_base = parameter_at(parameter_id);
    color_screen = parameter_at(parameter_id + 1);
    color_multiply = parameter_at(parameter_id + 2);
    atlas_rect = parameter_at(parameter_id + 3);
    vec4 mask_transform = parameter_at(parameter_id + 4);
    mesh_offset = mask_transform.xy;
    mask_scale = mask_transform.z;
    if (use_position_texture) VERTEX = texelFetch(position_texture, ivec2(position_id % 1024, position_id / 1024), 0).xy;
    vec2 mask_size = vec2(textureSize(tex_mask, 0)) / mask_scale;
    vec2 mask_vtx = VERTEX - mesh_offset;
    MASK_UV = (mask_vtx * mask_scale + atlas_rect.xy) / vec2(textureSize(tex_mask, 0));

	modulate = COLOR;
}

void fragment() {
    vec4 color_tex = texture(tex_main, UV);
    color_tex.rgb = color_tex.rgb * color_multiply.rgb;

    // premul alpha
    color_tex.rgb = color_tex.rgb + color_screen.rgb - (color_tex.rgb * color_screen.rgb);
    vec4 color_for_mask = color_tex * color_base * modulate;
    color_for_mask.rgb = color_for_mask.rgb * color_for_mask.a;

    vec4 clip_mask = texture(tex_mask, MASK_UV) * channel;
    vec2 mask_pixel = MASK_UV * vec2(textureSize(tex_mask, 0));
    if (any(lessThan(mask_pixel, atlas_rect.xy)) || any(greaterThan(mask_pixel, atlas_rect.xy + atlas_rect.zw))) {
        clip_mask = vec4(0.0);
    }

    float mask_val = clip_mask.r + clip_mask.g + clip_mask.b + clip_mask.a;
    color_for_mask = color_for_mask * (1.0 - mask_val);
    COLOR = color_for_mask;
}

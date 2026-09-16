// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2023 MizunagiKB <mizukb@live.jp>
// GDCubism shader: Add
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
    if (use_position_texture) VERTEX = texelFetch(position_texture, ivec2(position_id % 1024, position_id / 1024), 0).xy;
	modulate = COLOR;
}

void fragment() {
    vec4 color_tex = texture(tex_main, UV);
    color_tex.rgb = color_tex.rgb * color_multiply.rgb;

    // premul alpha
    color_tex.rgb = (color_tex.rgb + color_screen.rgb) - (color_tex.rgb * color_screen.rgb);
    vec4 color = color_tex * color_base * modulate;
    COLOR = vec4(color.rgb * color.a, 0.0);
}

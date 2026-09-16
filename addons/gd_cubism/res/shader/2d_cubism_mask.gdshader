// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2023 MizunagiKB <mizukb@live.jp>
// GDCubism shader: Mask
shader_type canvas_item;
uniform sampler2D position_texture : filter_nearest, repeat_disable;
uniform bool use_position_texture = false;
render_mode blend_mix, unshaded;

uniform vec4 channel : source_color;
uniform sampler2D tex_main : filter_linear_mipmap;

void vertex() {
    int position_id = int(round(VERTEX.x));
    if (use_position_texture) VERTEX = texelFetch(position_texture, ivec2(position_id % 1024, position_id / 1024), 0).xy;
}

void fragment() {
    COLOR = channel * texture(tex_main, UV).a;
}

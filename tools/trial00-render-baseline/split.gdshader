shader_type canvas_item;
render_mode unshaded;

uniform sampler2D left_view : filter_linear, repeat_disable;
uniform sampler2D right_view : filter_linear, repeat_disable;

void fragment() {
	float divider = 0.25 + (UV.y - 0.5) * tan(radians(10.0)) * 9.0 / 16.0;
	COLOR = UV.x < divider ? texture(left_view, UV) : texture(right_view, UV);
}

shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest;
instance uniform float group_opacity : hint_range(0.0, 1.0) = 1.0;

void fragment() {
	// CanvasGroup children are stored premultiplied in the backbuffer. Restore
	// straight alpha before returning the group result, matching Godot's built-in
	// CanvasGroup shader, then apply opacity once to the flattened character.
	vec4 color = textureLod(screen_texture, SCREEN_UV, 0.0);
	if (color.a > 0.0001) {
		color.rgb /= color.a;
	}
	color.a *= group_opacity;
	COLOR *= color;
}

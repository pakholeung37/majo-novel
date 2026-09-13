shader_type canvas_item;
render_mode unshaded;

uniform sampler2D source_image : filter_linear;
uniform sampler2D target_image : filter_linear;
uniform float progress : hint_range(0.0, 1.0) = 0.0;
uniform bool source_is_premultiplied = false;
uniform bool target_is_premultiplied = false;

varying vec4 item_modulate;

void vertex() {
	item_modulate = COLOR;
}

void fragment() {
	vec4 s = texture(source_image, UV);
	vec4 t = texture(target_image, UV);

	vec4 ps = source_is_premultiplied ? s : vec4(s.rgb * s.a, s.a);
	vec4 pt = target_is_premultiplied ? t : vec4(t.rgb * t.a, t.a);

	vec4 mixed = mix(ps, pt, progress >= 1.0 ? 1.0 : (progress <= 0.0 ? 0.0 : step(UV.x, progress)));

	if (mixed.a > 0.00001) {
		COLOR = vec4(mixed.rgb / mixed.a, mixed.a) * item_modulate;
	} else {
		COLOR = vec4(0.0, 0.0, 0.0, 0.0);
	}
}

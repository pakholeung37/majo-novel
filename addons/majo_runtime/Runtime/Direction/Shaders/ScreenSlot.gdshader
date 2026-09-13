shader_type canvas_item;
render_mode unshaded;

uniform vec2 screen_size;
uniform vec2 anchor;
uniform vec2 sample_size;
uniform vec4 clip_rect;
uniform float divider;
uniform float angle;
uniform bool right_side;

void fragment() {
	vec2 p = UV;
	float d = dot((p - vec2(divider, 0.5)) * screen_size, vec2(cos(angle), sin(angle)));
	float half_width = max(fwidth(d), 0.0001) * 0.5;
	float coverage = smoothstep(-half_width, half_width, d);
	// Main stays opaque underneath the inset's antialiased edge. Fading
	// both masks with source-over blending would expose the clear color.
	if ((right_side && d <= -half_width) || (!right_side && d >= half_width) ||
		p.x < clip_rect.x || p.y < clip_rect.y ||
		p.x >= clip_rect.z || p.y >= clip_rect.w) {
		discard;
	}
	vec2 source_uv = (p - anchor) / sample_size + vec2(0.5);
	if (any(lessThan(source_uv, vec2(0.0))) || any(greaterThan(source_uv, vec2(1.0)))) {
		COLOR = vec4(0.0, 0.0, 0.0, 1.0);
	} else {
		vec4 sample_color = texture(TEXTURE, source_uv);
		COLOR = sample_color.a > 0.00001 ? vec4(sample_color.rgb / sample_color.a, sample_color.a) : vec4(0.0);
	}
	if (right_side) {
		COLOR.a *= coverage;
	}
}

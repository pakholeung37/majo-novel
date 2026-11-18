shader_type canvas_item;
render_mode blend_mul, unshaded;

void fragment() {
	// Transparent texels multiply by white and leave the underlying layers alone.
	COLOR = vec4(mix(vec3(1.0), COLOR.rgb, COLOR.a), 1.0);
}

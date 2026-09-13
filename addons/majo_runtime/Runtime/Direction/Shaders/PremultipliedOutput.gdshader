shader_type canvas_item;
render_mode unshaded;
varying vec4 modulation;
void vertex() { modulation = COLOR; }
void fragment() {
    vec4 c = texture(TEXTURE, UV);
    COLOR = c.a > 0.00001 ? vec4(c.rgb / c.a, c.a) * modulation : vec4(0.0);
}

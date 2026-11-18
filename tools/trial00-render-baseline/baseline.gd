extends Node

const COURT = preload("res://court.gd")
const REFERENCE = Vector2(2560, 1440)
const WARMUP_US = 3_000_000

var layout := "direct"
var sample_seconds := 12.0
var output_dir := ""
var court := COURT.new()
var cameras: Array[Viewport] = []
var frames: Array[Dictionary] = []
var scene_load_us := 0
var started_us := 0
var sample_start_us := 0
var previous_us := 0
var camera_motion := true
var saving := false

func _ready() -> void:
	var load_start_us := Time.get_ticks_usec()
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--layout="):
			layout = argument.get_slice("=", 1)
		elif argument.begins_with("--seconds="):
			sample_seconds = argument.get_slice("=", 1).to_float()
		elif argument.begins_with("--output="):
			output_dir = argument.substr("--output=".length())
		elif argument == "--static":
			camera_motion = false
	if layout not in ["direct", "single", "split"] or sample_seconds < 0:
		push_error("Unsupported Trial00 render baseline options.")
		get_tree().quit(1)
		return
	get_window().title = "Trial00 Render Baseline | " + layout
	get_window().grab_focus()
	if layout == "direct":
		add_child(court)
		cameras.append(get_viewport())
	else:
		# A disabled world owner and one shared Stage, sampled independently by each View.
		var stage := SubViewport.new()
		stage.world_2d = World2D.new()
		stage.render_target_update_mode = SubViewport.UPDATE_DISABLED
		add_child(stage)
		stage.add_child(court)
		add_camera(stage.world_2d)
		if layout == "split":
			add_camera(stage.world_2d)
		add_output()
	if not court.populate():
		push_error("Could not populate the Trial00 court.")
		get_tree().quit(1)
		return
	started_us = Time.get_ticks_usec()
	scene_load_us = started_us - load_start_us
	update_cameras(0.0)

func add_camera(world: World2D) -> void:
	var viewport := SubViewport.new()
	viewport.world_2d = world
	viewport.size = get_window().size
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	cameras.append(viewport)

func add_output() -> void:
	var output := TextureRect.new()
	output.texture = cameras[0].get_texture()
	output.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	output.mouse_filter = Control.MOUSE_FILTER_IGNORE
	output.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	if layout == "split":
		var material := ShaderMaterial.new()
		material.shader = preload("res://split.gdshader")
		material.set_shader_parameter("left_view", cameras[0].get_texture())
		material.set_shader_parameter("right_view", cameras[1].get_texture())
		output.material = material
	add_child(output)
	get_window().size_changed.connect(resize_cameras)

func resize_cameras() -> void:
	for camera: SubViewport in cameras:
		camera.size = get_window().size

func update_cameras(seconds: float) -> void:
	var drift := sin(seconds * 0.9) * 95.0 if camera_motion else 0.0
	for index in cameras.size():
		var camera := cameras[index]
		var size := camera.get_visible_rect().size
		var center := Vector2(900 + drift, 610)
		var anchor := size / 2.0
		if layout == "split":
			center.x = -900 + drift if index == 0 else 900 + drift
			anchor.x = size.x * (0.13 if index == 0 else 0.61)
		var zoom := minf(size.x / REFERENCE.x, size.y / REFERENCE.y) * 1.6
		camera.canvas_transform = Transform2D(0.0, Vector2.ONE * zoom, 0.0, anchor - center * zoom)

func _process(_delta: float) -> void:
	if started_us == 0 or saving:
		return
	var now := Time.get_ticks_usec()
	update_cameras(float(now - started_us) / 1_000_000.0)
	if sample_seconds == 0.0 or now - started_us < WARMUP_US:
		return
	if sample_start_us == 0:
		sample_start_us = now
	if previous_us != 0:
		frames.append({
			"elapsed_ms": (now - sample_start_us) / 1000.0,
			"gap_ms": (now - previous_us) / 1000.0,
			"focused": get_window().has_focus(),
			"draw_calls": Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME),
		})
	previous_us = now
	if now - sample_start_us >= sample_seconds * 1_000_000.0:
		saving = true
		write_results()

func write_results() -> void:
	var file := FileAccess.open(output_dir.path_join("result.json"), FileAccess.WRITE)
	if file == null:
		push_error("Cannot write baseline result: " + output_dir)
		get_tree().quit(1)
		return
	var screen := get_window().current_screen
	var data := {
		"layout": layout,
		"engine": Engine.get_version_info().string,
		"rendering_method": RenderingServer.get_current_rendering_method(),
		"rendering_driver": RenderingServer.get_current_rendering_driver_name(),
		"window_size": [get_window().size.x, get_window().size.y],
		"screen": screen,
		"refresh_hz": DisplayServer.screen_get_refresh_rate(screen),
		"vsync": DisplayServer.window_get_vsync_mode(),
		"camera_motion": camera_motion,
		"viewports": cameras.size(),
		"actors": court.actor_count,
		"layers": court.layer_count,
		"atlases": court.atlases.size(),
		"scene_load_ms": scene_load_us / 1000.0,
		"frames": frames,
	}
	file.store_string(JSON.stringify(data, "\t") + "\n")
	file.close()
	# Read back a verification image after the sampling window has closed.
	await RenderingServer.frame_post_draw
	var error := get_viewport().get_texture().get_image().save_png(output_dir.path_join("final.png"))
	if error != OK:
		push_error("Could not save the baseline verification image.")
	get_tree().quit(0 if error == OK else 1)

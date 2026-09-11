extends SceneTree
## GPU regression probe for the Direction host's shared-world viewport topology.
## Run with --script res://tests/rendering/shared_world_mask_order.gd.
## -- --without-dependency reproduces the original one-frame mask lag.

func viewport(parent: Node) -> SubViewport:
	var result := SubViewport.new()
	result.size = Vector2i(16, 16)
	result.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	parent.add_child(result)
	return result

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	var stage := viewport(root)
	var view := viewport(root)
	var composite := viewport(view)
	var content := viewport(composite)
	var screen := viewport(root)
	RenderingServer.viewport_set_parent_viewport(view.get_viewport_rid(), screen.get_viewport_rid())
	var secondary := viewport(root)
	RenderingServer.viewport_set_parent_viewport(secondary.get_viewport_rid(), screen.get_viewport_rid())
	var secondary_composite := viewport(secondary)
	var secondary_content := viewport(secondary_composite)
	var mask := viewport(stage)
	var source := ColorRect.new()
	source.size = Vector2(16, 16)
	mask.add_child(source)
	var mesh_standin := TextureRect.new()
	mesh_standin.texture = mask.get_texture()
	stage.add_child(mesh_standin)
	content.world_2d = stage.world_2d
	secondary_content.world_2d = stage.world_2d
	if not "--without-dependency" in OS.get_cmdline_user_args():
		RenderingServer.viewport_set_parent_viewport(stage.get_viewport_rid(), content.get_viewport_rid())
	var stale := 0
	for frame in range(24):
		await process_frame
		var expected := Color.RED if frame % 2 == 0 else Color.BLUE
		source.color = expected
		await RenderingServer.frame_post_draw
		if frame < 2:
			continue
		for target in [content, secondary_content]:
			var actual: Color = target.get_texture().get_image().get_pixel(8, 8)
			if absf(actual.r - expected.r) > 0.1 or absf(actual.b - expected.b) > 0.1:
				stale += 1
	print("SHARED_WORLD_MASK_ORDER: stale samples = %d / 44" % stale)
	quit(0 if stale == 0 else 1)

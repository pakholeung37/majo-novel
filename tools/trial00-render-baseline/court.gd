extends Node2D
## Draws the existing court textures and the active atlas layers with stock Godot nodes.

const ASSETS = "res://demos/manosaba/assets"
const SEATS = ["Ema", "Hanna", "Sherry", "Leia", "Alisa", "Coco", "Miria", "", "", "Margo", "Nanoka", "AnAn", "Meruru"]
const SEAT_WIDTH = 1800.0

var actor_count := 0
var layer_count := 0
var atlases := {}
var multiply_material := ShaderMaterial.new()

func populate() -> bool:
	var wall := load(ASSETS + "/backgrounds/Court_Wall.png") as Texture2D
	var stand := load(ASSETS + "/objects/Court_Stand.png") as Texture2D
	if wall == null or stand == null:
		return false
	multiply_material.shader = preload("res://multiply.gdshader")
	for seat in range(-2, 13):
		var panel := Sprite2D.new()
		panel.texture = wall
		panel.centered = false
		panel.position = Vector2(seat * SEAT_WIDTH, -216)
		panel.scale = Vector2(SEAT_WIDTH, SEAT_WIDTH) / wall.get_size()
		add_child(panel)
	for index in SEATS.size():
		var x := (index + 0.5) * SEAT_WIDTH if index != 12 else -SEAT_WIDTH / 2.0
		if SEATS[index] != "" and not add_actor(SEATS[index], x):
			return false
		var lectern := Sprite2D.new()
		lectern.texture = stand
		lectern.position = Vector2(x, 1140)
		lectern.scale = Vector2.ONE * 1.8
		lectern.z_index = 100
		add_child(lectern)
	return true

func add_actor(actor: String, x: float) -> bool:
	var directory := "%s/characters/%s/" % [ASSETS, actor]
	var manifest = JSON.parse_string(FileAccess.get_file_as_string(directory + "layered_character.json"))
	if not manifest is Dictionary:
		push_error("Invalid layer manifest for " + actor)
		return false
	var layers: Array = manifest.layers
	var active := {}
	for layer: Dictionary in layers:
		if layer.enabled:
			active[layer.path] = true
		var atlas_path: String = directory + layer.atlas
		if not atlases.has(atlas_path):
			var atlas := load(atlas_path) as Texture2D
			if atlas == null:
				return false
			atlases[atlas_path] = atlas
	if not apply_appearance(manifest.defaultAppearance, manifest.compositionMap, layers, active):
		push_error("Unsupported default appearance for " + actor)
		return false

	layers.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
		if a.sortingOrder == b.sortingOrder:
			return a.path < b.path
		return a.sortingOrder < b.sortingOrder)
	var group := CanvasGroup.new()
	group.position = Vector2(x, 936)
	group.scale = Vector2.ONE * 0.48
	add_child(group)
	var canvas: Dictionary = manifest.canvas
	for layer: Dictionary in layers:
		if not active.has(layer.path):
			continue
		var sprite := Sprite2D.new()
		sprite.texture = atlases[directory + layer.atlas]
		sprite.region_enabled = true
		sprite.region_filter_clip_enabled = true
		var region: Dictionary = layer.region
		sprite.region_rect = Rect2(region.x, region.y, region.width, region.height)
		sprite.position = Vector2(layer.position.x - canvas.offsetX, canvas.offsetY - layer.position.y) * canvas.pixelsPerUnit
		sprite.offset = Vector2((0.5 - layer.pivot.x) * region.width, (layer.pivot.y - 0.5) * region.height)
		sprite.rotation_degrees = -layer.rotationDegrees
		sprite.scale = Vector2(layer.scale.x, layer.scale.y)
		sprite.flip_h = layer.flipX
		sprite.flip_v = layer.flipY
		sprite.modulate = Color(layer.color.r, layer.color.g, layer.color.b, layer.color.a)
		if "multiply" in layer.material.to_lower():
			sprite.material = multiply_material
		group.add_child(sprite)
		layer_count += 1
	actor_count += 1
	return true

func apply_appearance(appearance: String, aliases: Dictionary, layers: Array, active: Dictionary, depth := 0) -> bool:
	if depth >= 32:
		return false
	for item in appearance.split(",", false):
		var expression := item.strip_edges()
		if aliases.has(expression):
			if not apply_appearance(aliases[expression], aliases, layers, active, depth + 1):
				return false
			continue
		if expression == "-":
			active.clear()
			continue
		if expression == "+":
			for layer: Dictionary in layers:
				active[layer.path] = true
			continue
		var matched := false
		for index in expression.length():
			var operation := expression[index]
			if operation not in ["+", "-", ">", "~"]:
				continue
			var group := expression.left(index)
			var member := expression.substr(index + 1)
			var path := group.path_join(member) if not group.is_empty() and not member.is_empty() else group + member
			if not layers.any(func(layer: Dictionary) -> bool: return in_group(layer.path, path)):
				continue
			if operation in [">", "~"]:
				var replaced := group if not member.is_empty() else group.get_base_dir()
				for key: String in active.keys():
					if in_group(key, replaced):
						active.erase(key)
			for layer: Dictionary in layers:
				if in_group(layer.path, path):
					if operation == "-":
						active.erase(layer.path)
					else:
						active[layer.path] = true
			matched = true
			break
		if not matched:
			return false
	return true

func in_group(path: String, group: String) -> bool:
	return group.is_empty() or path == group or path.begins_with(group + "/")

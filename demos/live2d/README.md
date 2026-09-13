# Live2D demo

This demo integrates the Nijiiro Mao Cubism sample as a first-class Majo actor.

- Scene: `res://demos/live2d/Live2DDemo.tscn`
- Actor factory: `live2d-character`
- Durable appearance state: semantic expression aliases such as `smile` and `shocked`
- Transient action command: `<<live2d motion mao motion:wave>>`

Without an explicit actor definition, a model is discovered when
`characters/<id>/runtime/` contains exactly one `*.model3.json` file. The
conventional appearance `Default` clears expressions; model expression names
and `group:index` motion names are also available. Use an explicit definition
for semantic aliases and custom display placement. The preload window loads
the model's referenced textures ahead of actor creation; the Cubism model and
motion files are still loaded by `gd_cubism` when the actor is created.

Run it on macOS arm64 with Godot 4.7.2 Mono:

```bash
/Applications/Godot_mono.app/Contents/MacOS/Godot \
  --path /Users/pakholeung/web/majo-rebuild \
  res://demos/live2d/Live2DDemo.tscn
```

Run the complete non-interactive script smoke check with:

```bash
/Applications/Godot_mono.app/Contents/MacOS/Godot \
  --headless \
  --path /Users/pakholeung/web/majo-rebuild \
  res://demos/live2d/Live2DDemo.tscn \
  -- --live2d-auto
```

The bundled `gd_cubism` binary is the debug macOS arm64 build from the working
`manosaba-live2d` checkout. Redistribution remains subject to the gd_cubism,
Live2D Cubism SDK, and Nijiiro Mao sample-model licenses. See the model's
`assets/characters/mao/ReadMe.txt` for its usage terms.

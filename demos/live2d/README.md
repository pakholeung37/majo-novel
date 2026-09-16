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

Add `--live2d-auto-capture` to save one rendered screenshot per Yarn line under
`artifacts/live2d-yarn/<timestamp>/`. This verifies the real Direction
SubViewport pipeline in addition to command completion.

Run the rendered performance test with at least 20 simultaneous Cubism actors:

```bash
/Applications/Godot_mono.app/Contents/MacOS/Godot \
  --path /Users/pakholeung/web/majo-rebuild \
  res://demos/live2d/Live2DDemo.tscn \
  -- --live2d-stress --live2d-stress-count=20
```

The test warms up for three seconds, then records five seconds with VSync and
five seconds uncapped. It writes `results.json` and a rendered screenshot under
`artifacts/live2d-stress/<timestamp>/`. The count argument cannot be below 20.
For bottleneck isolation, `--live2d-stress-mask-size=<pixels>` caps each mask
viewport and `--live2d-stress-static` freezes model evaluation after warmup.
Reports include mesh, mask-viewport, and aggregate mask-pixel counts.

The bundled `gd_cubism` binary is the debug macOS arm64 build from the working
`manosaba-live2d` checkout. Redistribution remains subject to the gd_cubism,
Live2D Cubism SDK, and Nijiiro Mao sample-model licenses. See the model's
`assets/characters/mao/ReadMe.txt` for its usage terms.

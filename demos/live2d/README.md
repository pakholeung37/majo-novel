# Live2D demo

This demo integrates the Nijiiro Mao Cubism sample as a first-class Majo actor.

- Scene: `res://demos/live2d/Live2DDemo.tscn`
- Actor factory: `live2d-character`
- Durable appearance state: semantic expression aliases such as `smile` and `shocked`
- Transient action command: `<<live2d motion mao motion:wave>>`

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

# GPU rendering probes

Build the C# project, then run with a real renderer (not `--headless`):

```sh
dotnet build
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . res://tests/rendering/LiveDissolveProbe.tscn
```

`LiveDissolveProbe` exercises the camera backend's same-Stage dissolve. It checks
that both input textures reflect alternating colors in the same rendered frame,
that the temporary camera is absent from public View IDs, and that finishing,
replacement, and rollback return render-target counts to baseline.

Same-Stage camera dissolves use a temporary camera sharing the Stage's World2D.
The source retains its old camera pose while actors continue updating. Cross-Stage
transitions and state replacement retain the existing snapshot behavior.

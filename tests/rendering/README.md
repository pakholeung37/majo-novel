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

`ResolutionProbe.tscn` checks actual GPU output while resizing a window between
16:9 and 4:3. It verifies native window pixel density, unchanged logical Stage
coordinates and camera pose, correctly placed actor pixels, live dissolve source
resizing, and the imported Live2D texture's mipmap chain:

```sh
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . res://tests/rendering/ResolutionProbe.tscn
```

The Direction renderer keeps authored Stage coordinates independent of its
render-target sizes. Views render at least at reference density and increase
density to follow the hosting viewport's stretch scale; Screen retains the
window aspect ratio. Larger windows therefore cost more render-target memory
and GPU work. The Live2D atlas generates mipmaps to filter minified line art.

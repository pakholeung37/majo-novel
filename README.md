# Majo Novel

A script-driven visual novel runtime for **Godot .NET**, built with **C#** and **Yarn Spinner**.

[简体中文](README.zh-CN.md) · [Script writing guide](docs/script-writing.md) · [MIT license](LICENSE)

Majo Novel brings dialogue, character performances, cameras, transitions, audio, and saved state together in a modular runtime. Write the story and direction in Yarn; use C# to configure the host, add commands, or implement new actor types.


## What it can do

- **Story scripting:** dialogue, choices, variables, branching, and subroutines through Yarn Spinner, with Majo commands for visual direction and audio.
- **Visual direction:** backgrounds, characters, objects, appearance changes, transitions, camera movement, zoom, rotation, and effects. Stages and views support multiple scenes and camera views on screen.
- **Coordinated performances:** group direction commands into a `beat`, then choose whether the script waits for the performance to finish.
- **Different actor types:** the demos use layered characters, animated sprite sheets, Live2D characters, and custom animated backgrounds through actor plugins.
- **Player features:** text reveal, history, auto advance, read-only skip, save/load, settings, and dialogue travel to earlier lines when enabled.
- **Supporting systems:** separate dialogue and voice locales, resource preloading, script hot reload during development, and runtime diagnostics.

## A little Yarn, a complete performance

This excerpt uses the actor and resource configuration from the [runtime demo](demos/demo-01/assets/scripts/main.yarn):

```yarn
title: Start
scene: runtime_demo
---
<<bgm demo-theme track:music duration:0.35>>

<<beat begin>>
<<backdrop backdrop/courtroom via:dissolve duration:0.45>>
<<character hero appearance:calm at:left via:fade duration:0.45>>
<<beat end wait:true>>
Maya: The scene is ready. What would you like to investigate?

<<beat begin>>
<<character hero appearance:determined via:dissolve duration:0.35>>
<<character move hero at:center duration:0.35>>
<<beat end wait:true>>

<<camera move center:48,50 zoom:1.08 duration:0.25 wait:true>>
-> Press the witness
    Maya: Let's hear that testimony again.
-> Present evidence
    Maya: Something here doesn't add up.
===
```

Yarn controls the story flow. Majo's direction commands control the presentation; `beat` groups coordinated changes, and `wait:true` holds script progression until the performance completes. See the [script writing guide](docs/script-writing.md) for command syntax and more examples. The guide is currently in Chinese.

## Demos

| Demo | What it explores | Entry scene |
| --- | --- | --- |
| Runtime validation | Dialogue, direction, audio, branching, save/load, history, settings, and diagnostics using the included test assets | [`RuntimeDemo.tscn`](demos/demo-01/RuntimeDemo.tscn) |
| Manosaba | Layered character expressions, scripted scenes, and courtroom direction with multiple stages and views | [`ManosabaDemo.tscn`](demos/manosaba/ManosabaDemo.tscn) |
| Ace Attorney | Animated sprite sheet characters, courtroom staging, sound cues, and objection effects | [`AceAttorneyDemo.tscn`](demos/ace-attorney/AceAttorneyDemo.tscn) |
| Live2D | Cubism expressions and motions integrated with Majo's character and camera commands | [`Live2DDemo.tscn`](demos/live2d/Live2DDemo.tscn) |
| Background extension | A custom actor plugin with animated backgrounds and live transitions | [`BackgroundExtensionDemo.tscn`](demos/background-extension/BackgroundExtensionDemo.tscn) |

The runtime validation demo is the runnable reference included in this repository. Manosaba, Ace Attorney, and Live2D are showcases; their external asset packs are not included as complete runnable demos. Their scripts and integration code are available to inspect. They are not affiliated with or endorsed by the original games or model creators.

## Run the runtime validation demo

The current project targets **Godot 4.7.2 .NET** and **.NET 8**. Use the .NET edition of Godot, then build the project and run the scene explicitly; the project's default scene is the Manosaba showcase.

```sh
dotnet build "Majo Novel.csproj"
godot --path . res://demos/demo-01/RuntimeDemo.tscn
```

Replace `godot` with the path to your Godot .NET executable. You can also open `project.godot`, build the C# project, open the runtime demo scene, and run the current scene with F6.

## Code and documentation

The reusable runtime lives in [`addons/majo_runtime`](addons/majo_runtime); demo-specific presentation and integrations live in [`demos`](demos).

- [Script writing guide](docs/script-writing.md): Yarn syntax, direction commands, stages/views, cameras, audio, and examples.
- [Scene resource preparation](docs/advanced/scene-resources-injection.md): prepare or generate resources at scene boundaries and during state restoration.
- [Background extension example](demos/background-extension/README.md): implement an actor plugin using the public factory and resource interfaces.
- [Ace Attorney integration notes](demos/ace-attorney/README.md) and [Live2D integration notes](demos/live2d/README.md).
- [Runtime services and plugins](addons/majo_runtime/Runtime) and [test suites](tests).

Most detailed documentation is currently in Chinese. [`docs/archive`](docs/archive) contains historical plans and implementation notes; those documents may describe older APIs.

## Development checks

```sh
./tools/quality.sh --check  # Verify C# formatting
./tools/test.sh --unit     # Build and run C# unit tests
./tools/test.sh --fast     # Unit tests and Godot headless integration checks
./tools/test.sh --full     # Full checks, independent consumer, and export smoke test
```

The test script defaults to `--full`. For Godot checks, set `GODOT_BIN` to your Godot .NET executable if it is not at `/Applications/Godot_mono.app/Contents/MacOS/Godot`. Full checks also require matching export templates. Rendering-specific regression instructions are in [`tests/rendering`](tests/rendering/README.md).

## License and third-party content

Original Majo Novel code and documentation are available under the [MIT License](LICENSE), copyright © 2026 Pakho Leung.

Third-party components retain their own licenses. Game scenario text, artwork, music, voices, fonts, and Live2D models are not granted an MIT license by this project. In particular, the adapted Manosaba scenario text in the demo scripts is third-party content. See [third-party notices](THIRD_PARTY_NOTICES.md) for the boundaries and bundled component notices.

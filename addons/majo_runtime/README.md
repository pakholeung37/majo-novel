# Majo Runtime

A C# visual novel runtime for **Godot 4.7.2 .NET**, targeting **.NET 8**.

## Install

1. Extract the addon archive into your Godot .NET project. The `with-yarn` archive includes the official **YarnSpinner-Godot 0.3.22** dependency; for the standalone archive, install that version separately at `addons/YarnSpinner-Godot`.
2. Add both imports to your game's `.csproj`, inside its `<Project>` element:

   ```xml
   <Import Project="addons\YarnSpinner-Godot\YarnSpinner-Godot.props" />
   <Import Project="addons\majo_runtime\MajoRuntime.props" />
   ```

   Keep `TargetFramework` set to `net8.0` and `EnableDynamicLoading` set to `true`. Include the addon Bridge and SourceGenerator projects; the imports configure their references and source exclusions.
3. Build the C# project, then enable **Yarn Spinner** and **Majo Runtime** in Project Settings > Plugins. Complete the editor's initial import before running.

The dependency is unmodified upstream code. Majo's bridges require **Yarn Spinner 3.2.0** and the pinned Godot adapter; updating either dependency requires bridge compatibility checks.

## Minimal scene

Use this inside your scene's C# `_Ready` method:

```csharp
var runtime = new Majo.Runtime.MajoRuntimeBootstrap
{
    Configuration = Majo.Runtime.Configuration.MajoProjectConfiguration.CreateDefault(),
};
AddChild(runtime);
await runtime.InitializationCompletion;
await runtime.Host!.Services.Get<Majo.Runtime.Dialogue.IDialoguePlayer>().StartAsync(
    new Majo.Runtime.Dialogue.DialogueStartRequest("main", "Start"));
```

The default resource root is `res://assets`. Add `assets/scripts/main.yarnproject`:

```json
{
  "projectFileVersion": 3,
  "sourceFiles": ["main.yarn"],
  "baseLanguage": "en"
}
```

And `assets/scripts/main.yarn`:

```yarn
title: Start
---
Narrator: Hello. #line:intro-hello
===
```

Majo generates a `.yarnproject.direction.tres` companion during import. Enable both plugins during export so this companion and its scripts are included. Exported games use loaded-assembly compatibility checks and need no source `.props` or `plugin.cfg` files. Source hot reload is disabled when project source files are unavailable, unless an explicit external source directory is configured for an exported debug build.

Keep build inputs in the source project. Majo's Bridge and SourceGenerator project directories have `.gdignore` files; add `addons/YarnSpinner-Godot/SourceGenerator/*` to the game's export exclusion filter to exclude Yarn's generator sources and build caches from the game PCK.

## Saved state

Checkpoints are captured before line presentation. Nested and dynamic `detour` calls preserve their return frames and operands. Nested checkpoints require the same compiled program; top-level checkpoints can use compatible stable line IDs after script edits. Checkpoint format v3 reads legacy v2 payloads; older runtimes reject v3 checkpoints.

## Validation and distribution

The repository's full gate runs unit tests, Godot integration tests, and a clean consumer installed from the release ZIP. It also boots that consumer from a PCK and requires successful initialization and dialogue completion. macOS has local validation; a Linux CI workflow is provided. Other platforms have not been validated.

Build release ZIPs with `python3 tools/package-addon.py` from the repository root. Runtime ZIPs exclude demos, showcase assets, native Live2D integrations, build outputs and caches.

Original Majo code is [MIT licensed](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md).

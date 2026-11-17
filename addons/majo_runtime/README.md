# Majo Runtime

A C# visual novel runtime for **Godot 4.7.2 .NET**, targeting **.NET 8**.

## Install a published candidate

Requirements: Godot 4.7.2 .NET, .NET 8 SDK, Python 3.10+ and network access for the release and initial NuGet restore. No system repository clone is needed:

```sh
curl -fsSL https://github.com/pakholeung37/majo-novel/releases/download/v0.1.0-rc.2/install.py | python3 - init --version v0.1.0-rc.2 --project ./my-game --name MyGame
cd my-game
dotnet build
```

Open `project.godot`, let imports finish, then run. The bootstrap selects an explicit version, downloads its bundled addon and SHA256SUMS, verifies the archive, and configures the host through that release's manager. It does not silently follow latest/main.

For an existing game, use `install` instead of `init`. Before upgrades, stop source watching and commit your game. Validate a candidate before switching:

```sh
curl -fsSL https://github.com/pakholeung37/majo-novel/releases/download/v0.1.0-rc.2/install.py | python3 - upgrade --version v0.1.0-rc.2 --project ./my-game --check --godot /path/to/godot --check-command "python3 tools/dev.py check"
```

`--check` builds/imports a disposable game copy and runs the game-owned regression profile there (or the explicit `--check-command` override). A failed check leaves the live addon and lock unchanged. Successful upgrades save the previous addons, lock and host configuration under `.majo/history` (keep this local directory out of Git):

```sh
python3 addons/majo_runtime/Tools/majo.py rollback --project . --list
python3 addons/majo_runtime/Tools/majo.py rollback --project . --check --godot /path/to/godot --check-command "python3 tools/dev.py check"
```

Rollback preserves story/assets outside addon folders, refuses conflicting host-configuration edits and restores the exact previous dependency lock. Rebuild/import the active game after switching. This does not roll back or migrate player saves. See `compatibility.json` and CHANGELOG for API and state-format boundaries.

## Separate game repository

See [安装、联调与升级指南](DEVELOPMENT.zh-CN.md) for the complete workflow. From the system checkout, create a sibling game with:

```sh
python3 addons/majo_runtime/Tools/majo.py init --project ../my-game --source . --name MyGame
```

The manager configures project imports and editor plugins, records content hashes in `majo.lock.json`, and supports guarded upgrades, `doctor`, and `watch` (source sync plus game build). Commit the copied addons and lock in the game repository. Keep game-specific code outside managed addon folders. C# changes require restarting the game; source sync does not provide C# hot reload.

## Install

1. Extract the addon archive into your Godot .NET project. The `with-yarn` archive includes the official **YarnSpinner-Godot 0.3.22** dependency; for the standalone archive, install that version separately at `addons/YarnSpinner-Godot`.
2. Add both imports to your game's `.csproj`, inside its `<Project>` element:

   ```xml
   <Import Project="addons\YarnSpinner-Godot\YarnSpinner-Godot.props" />
   <Import Project="addons\majo_runtime\MajoRuntime.props" />
   ```

   If your project treats warnings as errors, add `CS1998;SYSLIB1220;CS0618` to `NoWarn` for the pinned upstream adapter. Keep `TargetFramework` set to `net8.0` and `EnableDynamicLoading` set to `true`. Include the addon Bridge and SourceGenerator projects; the imports configure their references and source exclusions.
3. Add the script-library loader as the **first** autoload in `project.godot` (the managed installer does this automatically):

   ```ini
   [autoload]
   MajoAssemblies="*res://addons/majo_runtime/Build/MajoAssemblyLoader.cs"
   ```

4. Build the C# project, then enable **Yarn Spinner** and **Majo Runtime** in Project Settings > Plugins. Complete the editor's initial import before running. General services, the Majo Yarn backend, Bootstrap/editor composition and upstream scripts compile into `Majo.Runtime.Engine.dll`, `Majo.Runtime.Yarn.dll`, `Majo.Runtime.Godot.dll` and `YarnSpinner.Godot.dll`; the tool-enabled loader registers their original `res://` script paths before plugins and scenes load.

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

The repository's full gate runs unit tests, Godot integration tests, and a clean consumer installed from the release ZIP. It also boots that consumer from a PCK and requires successful initialization and dialogue completion. macOS has local validation and the Linux release gate has passed on GitHub Actions. Other platforms have not been validated.

The `majo-runtime-0.1.0-rc.2-example.zip` archive is a complete project with both addons, a minimal host, and an original Yarn story using the default UI. Extract it separately from an existing game.

Build release ZIPs with `python3 tools/package-addon.py` from the repository root. Runtime ZIPs exclude demos, showcase assets, native Live2D integrations, build outputs and caches.

Original Majo code is [MIT licensed](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md).

## Validated exports

Use the checked export entry point (requires Python 3 and a built project):

```sh
python3 addons/majo_runtime/Editor/export.py --godot /path/to/godot --project . --preset "Your preset" --output /path/to/new/game.pck --pack
```

Omit `--pack` to export a release player; install matching Godot export templates first. The command validates all Yarn/Direction assets, requires the editor plugin to run, stages outputs, and publishes only when Godot reports no errors. Existing output files are preserved and must be moved before exporting again. Godot's native export plugin API only reports Direction failures; use this command for a reliable failing exit status and to prevent invalid exports from being published.

Selected-scene exports include Majo's default UI, shaders and required Yarn scripts through the Majo export plugin. Add your dynamically loaded game assets and Yarn projects to the preset's selected resources or reference them from a selected scene; Majo does not automatically include every game asset.

The release gate runs published ExportRelease binaries from an isolated directory with a PCK and instantiates both default UI scenes. It uses the editor executable as an engine host; a standalone player and Windows/mobile platforms still require separate validation.

Install verified Godot 4.7.2 .NET export templates with `python3 addons/majo_runtime/Tools/setup_godot.py --templates`. On Linux x86_64, CI can also install the pinned editor with `--editor --github-env "$GITHUB_ENV"`. Downloads use official release URLs and pinned SHA-256 digests. The editor version is independently checked by the consumer game's tooling.

## Register game regressions

Commit `majo-check.json` at the game root:

```json
{
  "format": 1,
  "commands": [
    {"argv": ["{python}", "tools/dev.py", "check"], "requiredOutput": "LAST_BELL_SMOKE_OK", "timeoutSeconds": 180}
  ]
}
```

Use a command appropriate to your game; the example is Last Bell's runner. `{python}` selects the installer's Python executable. `upgrade --check` and `rollback --check` automatically discover this profile in the isolated copy. Both the exit status and required output must pass. Without a profile, checks cover C# build and editor/Direction import only. Commands execute trusted game code; keep smoke tests isolated from real player storage.

A changed API/engine dependency contract or declared breaking changes require migration review and `--allow-breaking --check`. This opt-in does not perform migrations. Save compatibility must be tested separately with historical save fixtures.

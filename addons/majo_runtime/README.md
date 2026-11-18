# Majo Runtime

A C# visual novel runtime for **Godot 4.7.2 .NET**, targeting **.NET 8**.

## User guides (Chinese)

[First scene](Docs/first-scene.zh-CN.md) · [Script writing](Docs/script-writing.md) · [Assets and host configuration](Docs/assets-and-host.zh-CN.md) · [Release and troubleshooting](Docs/release-and-troubleshooting.zh-CN.md)

## Install a published candidate

Install Godot 4.7.2 .NET, the .NET 8 SDK (8.0.200 or later in the 8.0 series), and Python 3.10 or later. The installer and the first NuGet restore need network access. Save [the rc.5 installer](https://github.com/pakholeung37/majo-novel/releases/download/v0.1.0-rc.5/install.py) in the directory where you want to create `my-game`. Use these commands to create and build the project.

```sh
python3 install.py init --version v0.1.0-rc.5 --project ./my-game --name MyGame
cd my-game
dotnet build MyGame.csproj
```

Open `project.godot`, let imports finish, then run the project. The bootstrap downloads the requested bundled addon and `SHA256SUMS`, verifies the archive, and configures the host through that release's manager. On Windows, use your Python 3 command, such as `py -3`, in place of `python3`.

For first-time installation into an existing Godot .NET game, run the following from the directory containing `install.py`. Set `--project` to the directory with `project.godot` and a single root `.csproj` targeting Godot.NET.Sdk 4.7.2 and `net8.0`.

```sh
python3 install.py install --version v0.1.0-rc.5 --project ./my-game
```

To upgrade a game that already has `majo.lock.json`, stop source watching and commit your game. Review the target version's [compatibility contract](compatibility.json) and [changelog](CHANGELOG.md). The manager requires `--allow-breaking --check` for an upgrade to rc.5 because this version declares breaking changes. Run the upgrade after reviewing the applicable migrations.

```sh
python3 install.py upgrade --version v0.1.0-rc.5 --project ./my-game --allow-breaking --check --godot "/path/to/Godot"
```

Replace `/path/to/Godot` with your Godot .NET executable. `--check` builds and imports a disposable game copy and validates its Direction assets. If your game has a regression profile, the manager also runs it there. See [Register game regressions](#register-game-regressions) to add your own checks.

Successful upgrades save the previous addons, lock and host configuration under `.majo/history`. Keep this local directory out of Git. From the game directory, list available rollback points and validate the most recent one before restoring it.

```sh
python3 addons/majo_runtime/Tools/majo.py rollback --project . --list
python3 addons/majo_runtime/Tools/majo.py rollback --project . --check --godot "/path/to/Godot"
```

Rollback preserves story and assets outside addon folders, refuses conflicting host-configuration edits, and restores the exact previous dependency lock. Rebuild and import the game after switching. Test historical player saves separately when changing runtime versions.

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

The full release gate runs unit and Godot integration tests, then builds a clean consumer installed from the release ZIP, imports its resources, and runs dialogue from an exported PCK.

| Platform | Verified workflows |
| --- | --- |
| Linux x64 and Windows x64 | The rc.4 release CI passed the full gate on both platforms, including PCK startup in the Godot .NET editor executable. [rc.4 release checks](https://github.com/pakholeung37/majo-novel/actions/runs/44225452820) |
| macOS on Apple Silicon | Local full-gate validation and a separate launch of Last Bell from a Universal macOS player on an Apple Silicon Mac. [Local validation](https://github.com/pakholeung37/majo-novel/blob/v0.1.0-rc.4/docs/release/local-validation.md) |

Run a standalone player from your own export on each platform you ship, especially for targets outside the configurations above.

The release's `-example.zip` archive is a complete project with both addons, a minimal host, and an original Yarn story using the default UI. Extract it into an empty directory, then follow the [first scene tutorial (Chinese)](Docs/first-scene.zh-CN.md) to add your own artwork, music, and dialogue.

Build release ZIPs with `python3 tools/package-addon.py` from the repository root. Runtime ZIPs exclude demos, showcase assets, native Live2D integrations, build outputs and caches.

Original Majo code is [MIT licensed](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md).

## Validated exports

Use the checked export entry point (requires Python 3 and a built project):

```sh
python3 addons/majo_runtime/Editor/export.py --godot /path/to/godot --project . --preset "Your preset" --output /path/to/new/game.pck --pack
```

Omit `--pack` to export a release player; install matching Godot export templates first. The command validates all Yarn/Direction assets, requires the editor plugin to run, stages outputs, and publishes only when Godot reports no errors. Existing output files are preserved and must be moved before exporting again. Godot's native export plugin API only reports Direction failures; use this command for a reliable failing exit status and to prevent invalid exports from being published.

Selected-scene exports include Majo's default UI, shaders and required Yarn scripts through the Majo export plugin. Add your dynamically loaded game assets and Yarn projects to the preset's selected resources or reference them from a selected scene; Majo does not automatically include every game asset.

The PCK test loads ExportRelease binaries from an isolated directory and instantiates both default UI scenes in the Godot .NET editor executable. Use the platform validation above when deciding which exports to test as standalone players.

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

Use a command that exists in your game. This example uses Last Bell's runner. `{python}` selects the installer's Python executable. `upgrade --check` and `rollback --check` automatically discover this profile in the isolated copy. Both the exit status and required output must pass. You can supply `--check-command` with your own command to override the profile. The default checks cover the C# build, editor import, and Direction validation.

A changed API/engine dependency contract or declared breaking changes require migration review and `--allow-breaking --check`. This opt-in does not perform migrations. Save compatibility must be tested separately with historical save fixtures.

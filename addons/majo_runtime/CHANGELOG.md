# Changelog

## Unreleased

- Isolate state notification failures from capture and restore outcomes, including input-lock cleanup.
- Activate commands and plugins inside the host lifecycle deadline; deactivate plugins before runtime dependencies close.
- Move rendering, replay, audio and skip coordination out of Core/RuntimeHost into a replaceable execution coordinator.
- Report plugin initialization rollback errors alongside the original failure.

## 0.1.0-rc.2

- Resolve temporary candidate paths before invoking MSBuild/Godot, fixing macOS C# resource mapping during checked upgrades.

- Discover game-owned `majo-check.json` regression profiles during checked upgrade and rollback.
- Require declared success output as well as exit 0, so missing game completion cannot pass.
- Record compatibility contracts in dependency locks and require explicit checked opt-in across API/engine dependency boundaries.
- Runtime API 0.1, Yarn dependencies and serialized state layouts remain compatible with rc.1; no player-save migration is performed.

## 0.1.0-rc.1

- Publish a version-pinned online installer alongside checksummed addon archives.
- Validate upgrades in an isolated consumer copy before changing the live project.
- Save local rollback points and restore exact dependency locks and host settings.
- Document API/state compatibility and preserve game-owned story and asset files.

## 0.1.0 — initial release preparation

- Isolate Yarn-specific behavior in Majo-owned program, presentation and Runner bridges, using unmodified YarnSpinner-Godot 0.3.22 and Yarn Spinner 3.2.0.
- Validate loaded Yarn assemblies during game startup so packed projects do not require source metadata.
- Preserve nested detour return frames and operands in checkpoint v3, while reading legacy v2 checkpoints.
- Require exact program fingerprints for nested restoration and defer incompatible live reloads until the next dialogue session.
- Validate release ZIP installation, first import, dialogue branching and actual PCK startup in the full test gate.
- Provide standalone and Yarn-bundled source addon ZIPs with licenses, release metadata and SHA-256 checksums.

- Align the release gate on .NET 8 and handle upstream compiler warnings without editing Yarn.
- Include runtime UI, shader and Yarn script dependencies in selected-scene exports.
- Provide a checked export command that rejects invalid Direction assets before publishing outputs.
- Run published Release assemblies without source fallback, test invalid exports, and instantiate packaged default UI scenes.
- Restrict source archives to the distributable addons and provide an original PNG icon, submission notes and a runnable example archive.

- Add managed game initialization, guarded addon installation/upgrades, source synchronization, build watching and dependency diagnosis with a content lock.

# Changelog

## Unreleased

- Honor replaced command adapters during Bootstrap activation, locale safety checks, backend observation and error forwarding; clean up their observers on shutdown.
- Verify replacement adapter initialization, recoverable failures, command drain and ownership in the real-engine replacement regression.
- Generate completion manifests with canonical LF line endings on Windows as well as Unix hosts.

- Use one Python test gate on macOS, Linux and Windows while preserving the existing shell entry point; retain per-step logs and machine-readable results.
- Require success markers for headless and consumer runs; timeouts cannot count as intentional failure-probe success.
- Select native Windows export presets and .NET runtime IDs, and support graceful Windows watch cancellation.
- Gate draft releases on both Linux and Windows full consumer/export checks with checksum-pinned Godot downloads.

- Compile general Godot services into Majo.Runtime.Engine and the Majo Yarn adapter into Majo.Runtime.Yarn; keep Bootstrap/editor composition in Majo.Runtime.Godot.
- Enforce that general engine services reference neither Yarn nor composition; the Yarn adapter uses only public engine APIs.
- Move node metadata, scene-entry requests and companion serialization into Domain, and command activity/adapter contracts into Commands without engine or Yarn dependencies.
- Register all three Majo script libraries before scene/plugin loading and validate their delivery in source and packed consumers.

- Compile the engine runtime and unmodified upstream Godot Yarn adapter into separate libraries; keep their generated script paths relative to the consuming Godot project.
- Register library scripts before plugins/scenes through a tool-enabled host autoload; preserve game-owned generated Yarn action registration.
- Compile demos and headless/rendering probes separately; runtime unit tests now reference the runtime library directly, while demo tests use their own project.
- Expose render-demand diagnostics, shared performance spans and state payload serialization as explicit extension APIs used by demos.

- Compile state persistence contracts/models, migrations, mutation gates, canonical serialization and file storage into Majo.Runtime.Persistence, referencing only Core and .NET.
- Compile resource contracts, resolution, cache/leases, retention and look-ahead windows into Majo.Runtime.Resources, referencing only Core and .NET; keep engine and locale-service adapters in the integration layer.
- Run persistence and resource regression tests without the game; verify actual dialogue snapshots, file storage and resource leases in installed and packed addon consumers.

- Compile command definitions, binding, generic converters, registration, resource contracts, manifests and wait groups into Majo.Runtime.Commands, referencing only Core and .NET.
- Move pure command tests and generated command integration tests out of the game project; add an independent test gate without Godot or Yarn.
- Verify consumer-defined generated commands through Yarn dispatch in both installed source addons and actual packed Release builds.

- Compile Direction models/planning, camera projection/coverage, actor definitions/requests and timing curves into Majo.Runtime.Domain, referencing only Core and .NET.
- Separate pure metadata resolution and prepared commit samples from Godot actor/renderer implementation details.
- Move domain tests out of the game-referencing test project; add a Godot-free test command and CI job, and include the domain assembly in source addon builds/exports.

- Separate Godot Direction surface ownership, screen composition, background transition inputs, retained restore output and camera projection from the render host.
- Release unactivated transition inputs when views are removed or reset, and cover cold disposal, repeated shutdown and screen-slot material ownership.
- Dispose temporary View/Screen readback images after copying them into owned textures.
- Add real-GPU render acceptance with pixel checks, 200 restore cycles and saved renderer/image evidence.

- Bind resource release to the acquired cache entry so late releases cannot evict a replacement entry after reset.

- Compile Core lifecycle and service contracts into an independent net8.0 assembly without Godot or Yarn references; run their tests directly against that assembly.
- Include the Core project and assembly in managed addon builds and exports; binary extensions must rebuild against the new assembly layout.

- Separate resource resolution, shared load/cache retention and lease lifecycle ownership behind the existing resource manager API.
- Release already created leases when preload group construction fails; wait for complete load workers before resetting or shutting down providers.

- Separate Yarn playback session completion, checkpoint replay and localized presentation state from the Runner backend.
- Separate hot reload source monitoring from candidate application and rollback; drain scans and notifications during shutdown, including cancellation callback failures.
- Verify that rejected live reloads preserve the public dialogue session and allow a later successful reload.

- Share catalog path resolution and typed argument binding between Yarn execution and resource preloading; support opt-in plugin resource providers.
- Preserve command casing rules, named argument order, quoted IDs, contract defaults and object aliases during preload analysis.
- Require the runtime command catalog for low-level preload helpers; migrate `Resources(tokens)` to `Resources(tokens, catalog)` or an attached planner's `GetResources(tokens)`.

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

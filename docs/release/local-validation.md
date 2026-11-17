# Local release validation — 2026-11-17

Environment: Apple Silicon macOS, Godot 4.7.2 .NET (`ed1daf0bf`), .NET SDK 8.0.204.

- 595 runtime unit tests and 10 command-generator tests passed.
- 10 addon-manager tests passed: guarded upgrade, game-file preservation, rollback, initialization failure, missing solution, unsafe archive/lock rejection, and live source synchronization.
- Full release gate passed: install release ZIP through manager, first import, doctor, reinstall/upgrade, branching and nested-detour dialogue, selected-resource export, isolated ExportRelease assembly/PCK boot, invalid Direction export rejection and disabled-plugin rejection.
- A separate local `last-bell` game repository was initialized through the manager. Its original Chinese opening story, default UI, local source sync, and checked export were configured.
- Official .NET export templates were installed after checking SHA-256 `92f8681e349ef1f90891b792da95e3b2b0bd1ed610b78018c58feb2d87e15a9d`.
- A Universal macOS player was exported, extracted outside both repositories, and launched from its own executable. The opening completed with `LAST_BELL_SMOKE_OK`, exit 0, with no runtime errors. This tests an actual template executable, separately from the automated PCK engine-host test.

## macOS settings discovered during validation

The verified 4.7.2 .NET template archive's macos.zip contains Universal binaries. The game's macOS preset uses `binary_format/architecture="universal"`, and enables ETC2/ASTC texture import in addition to the default desktop texture format.

The built-in ad-hoc signer produced a bundle accepted by `codesign --verify`, but macOS AMFI killed it with “failed parsing DER entitlements.” Selecting native Xcode codesign (`codesign/codesign=3`, `codesign/identity="-"`) fixed startup. The resulting development player is ad-hoc signed and not notarized. Developer ID distribution signing is outside this local test.

Remote Linux CI and Windows execution have not been run. The framework tag workflow creates draft releases only; neither repository has been pushed/published by this setup.

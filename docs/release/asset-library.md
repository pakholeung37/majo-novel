# Asset Library submission

The source archive is restricted by `.gitattributes` to Majo Runtime and the pinned Yarn addon. Verify the archive for the actual release commit before submitting; do not submit the showcase project as an addon.

- Name: Majo Runtime
- Category: Addons / Tools
- Version: 0.1.0 (release candidate; promote after release checks pass)
- Godot version: 4.7.2 .NET
- License: MIT
- Repository: https://github.com/pakholeung37/majo-novel
- Issues: https://github.com/pakholeung37/majo-novel/issues
- Icon: `addons/majo_runtime/Media/icon.png` (128 × 128 PNG). Use the raw.githubusercontent.com URL for the pinned release commit.
- Download commit: fill in the verified release commit, not a moving branch.

Description:

Majo Runtime is a C# visual novel runtime for Godot .NET and Yarn Spinner. It provides dialogue, choices, character presentation, camera and scene transitions, audio, localization, saved state and script hot reload. Installation requires two project imports and enabling both editor plugins. Includes unmodified YarnSpinner-Godot 0.3.22 with Yarn Spinner 3.2.0. Desktop .NET projects are the current target; only macOS has local runtime validation, and Linux CI must pass before Linux is claimed as tested. This release does not include showcase game content or native Cubism integrations.

Submission is manual. The release-check workflow uploads validated ZIP artifacts; it does not publish a release or submit to the Asset Library.

## GitHub release flow

Push a version tag matching plugin.cfg (`v0.1.0` or a candidate such as `v0.1.0-rc.1`). The Linux full gate must pass before the release job downloads its artifacts, verifies SHA256SUMS and creates a **draft** GitHub Release. Candidate tags are marked prerelease. The draft contains the standalone addon, bundled addon and runnable example ZIPs; review it before publishing. Reusing an existing release tag fails rather than replacing published assets.

The repository is not pushed or published by local tooling. Asset Library submission remains manual and must use the tested release commit.

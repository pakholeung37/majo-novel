# Changelog

## 0.1.0 — release candidate

- Isolate Yarn-specific behavior in Majo-owned program, presentation and Runner bridges, using unmodified YarnSpinner-Godot 0.3.22 and Yarn Spinner 3.2.0.
- Validate loaded Yarn assemblies during game startup so packed projects do not require source metadata.
- Preserve nested detour return frames and operands in checkpoint v3, while reading legacy v2 checkpoints.
- Require exact program fingerprints for nested restoration and defer incompatible live reloads until the next dialogue session.
- Validate release ZIP installation, first import, dialogue branching and actual PCK startup in the full test gate.
- Provide standalone and Yarn-bundled source addon ZIPs with licenses, release metadata and SHA-256 checksums.

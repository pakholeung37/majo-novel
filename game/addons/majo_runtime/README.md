# Majo Runtime

Reusable Godot C# visual novel runtime built around Yarn Spinner.

Compatibility baseline:

- Godot 4.7 C#
- YarnSpinner-Godot 0.3.22
- Yarn Spinner 3.2.0

Yarn Spinner is an external prerequisite and is not bundled with this plugin.

Current implementation status: M0 scaffolding and test infrastructure.

## Testing

Run the complete local gate from the repository root:

```sh
./tools/test.sh
```

The gate performs a non-incremental C# build, xUnit tests, Godot headless
import/compilation, runtime lifecycle integration tests, and a negative probe
that confirms failed headless assertions return a non-zero exit code.

Set `GODOT_BIN` when Godot Mono is installed outside the default macOS path:

```sh
GODOT_BIN=/path/to/godot ./tools/test.sh
```

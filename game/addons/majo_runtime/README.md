# Majo Runtime

Reusable Godot C# visual novel runtime built around Yarn Spinner.

Compatibility baseline:

- Godot 4.7 C#
- YarnSpinner-Godot 0.3.22
- Yarn Spinner 3.2.0

Yarn Spinner is an external prerequisite and is not bundled with this plugin.

Current implementation status: M2 Operation scheduling kernel.

## Runtime setup

Add `MajoRuntimeBootstrap` to the scene tree, assign a
`MajoProjectConfiguration`, and register services before adding the node to the
tree. Every registered service implements `IRuntimeService`; dependencies are
declared explicitly and resolved through the factory:

```csharp
var runtime = new MajoRuntimeBootstrap
{
    Configuration = MajoProjectConfiguration.CreateDefault(),
    ConfigureServices = services => services
        .AddSingleton<IMyService>(
            resolver => new MyService(resolver.Get<IRuntimeLifetime>()),
            typeof(IRuntimeLifetime)),
};

AddChild(runtime);
await runtime.InitializationCompletion;
```

The host initializes services in dependency order, resets them without
recreating instances, and shuts them down in reverse order. Runtime shutdown
cancels both runtime and session lifetime tokens; reset cancels and replaces
the session token. Services can explicitly depend on the built-in
`IRuntimeLifetime` to observe those tokens. Registration and configuration
failures report typed errors before the service graph starts.

## Operations

`MajoRuntimeBootstrap` registers `IFrameClock` and `IOperationScheduler` by
default. `Start` returns an `IOperationHandle` immediately; callers opt into
waiting through the handle or a typed ID/group target:

```csharp
var operations = runtime.Host!.Services.Get<IOperationScheduler>();
var fade = operations.Start(new OperationDefinition(
    context => context.FrameClock.DelayAsync(
        TimeSpan.FromSeconds(0.5), context.CancellationToken),
    id: "maya-fade",
    groupId: "scene-setup",
    completeAsync: _ => ApplyFinalStateAsync()));

await operations.WaitAsync(OperationTarget.ForId("maya-fade"));
```

`Cancel` stops without applying the final state and completes the handle as
cancelled. `Complete` interrupts natural execution, applies the registered
finalizer, and completes successfully. Faults remain observable through
`Completion`; the scheduler also observes and logs every background fault.
Reset cancels all active operations before accepting work for the next session.

Games can replace either default service in `ConfigureServices` with
`ReplaceSingleton` before the host builds.

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

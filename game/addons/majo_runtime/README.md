# Majo Runtime

Reusable Godot C# visual novel runtime built around Yarn Spinner.

Compatibility baseline:

- Godot 4.7 C#
- YarnSpinner-Godot 0.3.22
- Yarn Spinner 3.2.0

Yarn Spinner is an external prerequisite and is not bundled with this plugin.

Current implementation status: M4 Yarn Command adapter.

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

## Actors

Actor definitions live under `MajoProjectConfiguration.Actors`. Each definition
selects a factory (`character` or `background`), a default appearance, and one
or more appearance-to-texture mappings. Character definitions also select the
configured `Left`, `Center`, or `Right` position preset.

The bootstrap registers `IActorManager` by default:

```csharp
var actors = runtime.Host!.Services.Get<IActorManager>();
actors.Create("maya");
var maya = actors.Get<ICharacterActor>("maya");

maya.SetAppearance("happy");
var move = maya.ChangePosition(
    CharacterPositionPreset.Left,
    durationSeconds: 0.35,
    operationId: "maya-move");
await move.Completion;
```

Character views are `Sprite2D` nodes and backgrounds use full-rect
`TextureRect` views. A new animation targeting the same Actor property first
force-completes the previous animation, producing a deterministic starting
state. Actor reset destroys all views and cancels their owned operations.
Custom factories can be added or replace built-ins through
`ConfigureActorFactories` before Runtime initialization.

## Yarn commands

Assign the scene's `DialogueRunner` to `MajoRuntimeBootstrap.YarnDialogueRunner`
before adding the bootstrap to the tree. The Runtime then explicitly registers
five command handlers during initialization and removes them during shutdown:

```csharp
var runtime = new MajoRuntimeBootstrap
{
    Configuration = configuration,
    YarnDialogueRunner = dialogueRunner,
};
```

The frozen M4 positional syntax is:

```text
back <actor> <appearance> [duration] [wait] [group|-] [operation|-]
char <actor> <appearance> <left|center|right> [duration] [wait] [group|-] [operation|-]
hide <actor> [duration] [wait] [group|-] [operation|-]
await <target> [complete] [group|id]
cancel <target> [group|id]
```

Durations default to `Operations.DefaultAnimationDurationSeconds`; animated
commands default to `wait=false`. `-` leaves an optional group or operation ID
unset. `await` and `cancel` target a group by default, or an individual
operation when the final argument is `id`.

```yarn
<<back backdrop courtroom 0.5 false setup back-fade>>
<<char maya happy left 0.5 false setup maya-move>>
<<await setup false group>>

Maya: 准备完成了。

<<char maya surprised center 0.35 true - maya-center>>
Maya: 等一下！
```

The adapter deliberately does not register `wait` or `stop`, preserving Yarn's
built-in commands. Parsing failures include both the invalid parameter and the
exact command usage. The pure command core depends only on `IActorManager` and
`IOperationScheduler`, while the Yarn-specific layer is isolated behind an
explicit command registry.

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

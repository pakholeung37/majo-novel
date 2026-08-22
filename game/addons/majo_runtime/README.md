# Majo Runtime

Reusable Godot C# visual novel runtime built around Yarn Spinner.

Compatibility baseline:

- Godot 4.7 C#
- YarnSpinner-Godot 0.3.22
- Yarn Spinner 3.2.0

Yarn Spinner is an external prerequisite and is not bundled with this plugin.

Current implementation status: M5 minimum playable loop plus convention-based
Runtime resource loading.

## Installation

Copy `addons/majo_runtime` into the target Godot C# project's `addons`
directory, install the compatible YarnSpinner-Godot plugin separately, and
enable both plugins under **Project Settings > Plugins**. The complete local
installation, configuration, run, and test walkthrough is in
[`docs/plugin-quickstart.md`](../../../docs/plugin-quickstart.md).

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

`RuntimeConfiguration.LifecycleTimeoutSeconds` is a hard upper bound on how
long the bootstrap waits for each lifecycle call. At the deadline it requests
cancellation and returns a `TimeoutException`; a non-cooperative service task
continues to be observed until it eventually finishes, avoiding an unobserved
background fault. `RuntimeShutdown` is emitted exactly once when shutdown
starts, including direct scene-tree exit. Await `ShutdownCompletion` when the
caller needs to know that service cleanup has finished.

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
Completed IDs and groups remain awaitable for the current session, including
their terminal failure or cancellation; reset cancels active operations and
clears these completion records before accepting work for the next session.

Games can replace either default service in `ConfigureServices` with
`ReplaceSingleton` before the host builds.

## Actors

Actor definitions live under `MajoProjectConfiguration.Actors`. Each definition
selects a factory (`character` or `background`) and a default appearance.
Character definitions also select the configured `Left`, `Center`, or `Right`
position preset. Texture paths follow the resource conventions below; the
appearance list is optional and contains only explicit overrides.

The bootstrap registers `IActorManager` by default:

```csharp
var actors = runtime.Host!.Services.Get<IActorManager>();
actors.Create("maya");
var maya = actors.Get<ICharacterActor>("maya");

maya.SetAppearance("happy");
var move = maya.ChangePosition(
    CharacterPositionPreset.Left,
    new AnimationSpec(
        0.35,
        AnimationTransition.Cubic,
        AnimationEase.Out,
        conflictPolicy: AnimationConflictPolicy.CompletePrevious),
    operationId: "maya-move");
await move.Completion;
```

Character views are `Sprite2D` nodes and backgrounds use full-rect
`TextureRect` views. A new animation targeting the same Actor property first
force-completes the previous animation, producing a deterministic starting
state. Actor reset destroys all views and cancels their owned operations.
Custom factories can be added or replace built-ins through
`ConfigureActorFactories` before Runtime initialization. A custom actor can use
`ActorDefinitionConfiguration.FactoryConfiguration` for factory-owned data
instead of the built-in texture appearance schema. Factories validate that
payload through `ValidateDefinition`; asynchronous factories can override
`CreateAsync`, while concurrent requests for one Actor ID share one creation.

`IActor` exposes logical/presentation state but no longer exposes a concrete
Node. Godot-backed actors additionally implement `IGodotActorView`. Target
properties preserve the requested final state while a Tween is still rendering
an intermediate value.

## Resources

`IResourceManager` is a Runtime service with typed sync/async loading, scoped
loaders, cache lifecycle, concurrent-load deduplication, and prioritized
providers. The default Godot provider resolves extensionless logical keys under
`res://assets` and discovers both imported source formats and native Godot
resource formats.

Default conventions:

```text
res://assets/characters/<actor-id>/<appearance>.<texture-extension>
res://assets/backgrounds/<actor-id>/<appearance>.<texture-extension>
res://assets/scripts/<project-id>.yarnproject
```

An Actor therefore needs no physical texture paths:

```csharp
configuration.Actors!.Actors.Add(new ActorDefinitionConfiguration
{
    Id = "hero",
    FactoryId = ActorFactoryIds.Character,
    DefaultAppearance = "calm",
});
```

Load other resources by logical ID after Runtime initialization:

```csharp
var resources = runtime.Host!.Services.Get<IResourceManager>();
var yarnProject = await resources
    .CreateLoader<YarnProject>(configuration.Resources!.ScriptsPath)
    .LoadAsync("main");
```

`ResourceConfiguration` can change the root or category prefixes. A non-empty
`ActorAppearanceConfiguration.TexturePath` is a strict override: Runtime tries
that path only and reports all attempted candidates if it is missing. Custom
resource providers register through `ConfigureResourceProviders`; higher
priority providers can replace project assets without changing logical IDs.

## Yarn commands

Assign the scene's `DialogueRunner` to `MajoRuntimeBootstrap.YarnDialogueRunner`
before adding the bootstrap to the tree. The Runtime then explicitly registers
six command handlers during initialization and removes them during shutdown:

```csharp
var runtime = new MajoRuntimeBootstrap
{
    Configuration = configuration,
    YarnDialogueRunner = dialogueRunner,
};
```

Actor commands use one primary positional actor ID followed by order-independent
named parameters. Parameter names are case-insensitive; canonical command names
use camelCase:

```text
back <actor> appearance:<id> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]
char <actor> appearance:<id> position:<left|center|right> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]
hide <actor> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]
await [group:<id>|operation:<id>] [complete:<bool>]
endAwait
cancel <group:<id>|operation:<id>>
```

Durations default to `Operations.DefaultAnimationDurationSeconds`; animated
commands default to `wait:false`. `group` and `operation` are mutually exclusive
for `await` and `cancel`. Unknown, duplicated, missing, and unexpected positional
parameters fail the command with its exact usage.

```yarn
<<await>>
<<back backdrop appearance:courtroom duration:0.5 operation:back-fade>>
<<char maya appearance:happy position:left duration:0.5 operation:maya-move>>
<<endAwait>>

Maya: 准备完成了。

<<char maya appearance:surprised position:center duration:0.35 wait:true operation:maya-center>>
Maya: 等一下！
```

An `await` block collects operation handles directly, so custom commands are
included when they call `RuntimeCommandContext.Track`. Blocks may be nested.
`<<await complete:true>>` completes running operations at `<<endAwait>>` before
joining them. Targeted forms remain available as `<<await group:setup>>`,
`<<await operation:maya-move complete:true>>`, and
`<<cancel operation:maya-move>>`.

The adapter deliberately does not register `wait` or `stop`, preserving Yarn's
built-in commands. Parsing failures include both the invalid parameter and the
exact command usage. The pure command core depends only on `IActorManager` and
`IOperationScheduler`, while the Yarn-specific layer is isolated behind an
explicit command registry.

Custom Yarn commands register before initialization without changing Runtime
core source:

```csharp
runtime.ConfigureCommands = commands => commands.Register(
    "set_case_mode",
    async (arguments, context) =>
    {
        var actors = context.Require(context.Actors, "ActorManager");
        var services = context.Require(context.Services, "ServiceResolver");
        // Apply game-specific behavior through public Runtime services.
        await Task.CompletedTask;
    });
```

Every handler receives a `RuntimeCommandContext`. Operations tracked through
the context are cancelled when the current Yarn dialogue stops. Command
failures emit `RuntimeCommandFailed`, abort the current dialogue and remain
available through `IYarnCommandAdapter.LastFailure`.

## M5 playable demo

Run the project or open `res://demo/M5Demo.tscn`. The demo uses one Yarn script
to exercise parallel background/character animation, group join, a waiting
animation, dialogue, options, branching, a custom command, and clean Runtime
shutdown.

All extension code lives outside the plugin under `res://demo`:

- `M5CaseService` demonstrates a service with Runtime lifetime dependency.
- `M5PortraitActorFactory` demonstrates a custom Actor factory ID.
- `demo_route` demonstrates a custom Yarn command resolving the service.
- `M5DemoPresenter` supplies the visible line and option UI.
- `M5DialogueProbe` runs the same content without graphics for deterministic
  integration tests.

The main scene also has a non-interactive smoke mode:

```sh
/path/to/godot --headless --path game -- --m5-auto
```

## Testing

Run the complete local gate from the repository root:

```sh
./tools/test.sh
```

The gate performs scoped formatting/static checks, a non-incremental C# build,
xUnit tests, Godot headless import/compilation, three consecutive runtime/Yarn
integration runs, and a negative probe that confirms failed headless assertions
return a non-zero exit code.

Set `GODOT_BIN` when Godot Mono is installed outside the default macOS path:

```sh
GODOT_BIN=/path/to/godot ./tools/test.sh
```

# Majo Runtime

Reusable Godot C# visual novel runtime built around Yarn Spinner.

Compatibility baseline:

- Godot 4.7 C#
- YarnSpinner-Godot 0.3.22
- Yarn Spinner 3.2.0

Yarn Spinner is an external prerequisite and is not bundled with this plugin.

Current implementation status: M12 Durable State foundation, M11 Diagnostics,
M10.5 Distributability Gate, M10 Auto and Session History, M9 audio runtime,
M8 default VN presentation and Yarn markup bridge, M7 input ownership, M6
dialogue sessions, and the playable loop on the stable Runtime API.

## Installation

1. Copy `addons/majo_runtime` into the target Godot C# project's `addons` directory.
2. Install the compatible YarnSpinner-Godot plugin separately into `addons/YarnSpinner-Godot`.
3. In your project's `.csproj` file, import both plugins' MSBuild properties:

```xml
<Import Project="addons\YarnSpinner-Godot\YarnSpinner-Godot.props" />
<Import Project="addons\majo_runtime\MajoRuntime.props" />
```

4. In Godot, enable both plugins under **Project Settings > Plugins**.
5. Build the C# solution (`dotnet build`) and perform an initial Godot editor import.

The complete local installation, configuration, run, and test walkthrough is
available under `docs/plugin-quickstart.md`; the current runtime API is documented below.

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

## Input

Runtime registers a Yarn-free `IInputManager`; Presenter, menu, and host gameplay
code acquire logical actions instead of implementing competing Godot `_Input`
callbacks:

```csharp
var input = runtime.Host!.Services.Get<IInputManager>();
using var dialogueInput = input.AcquireScope(
    presenter,
    new InputScopeOptions(
        [MajoInputActions.Advance, MajoInputActions.Auto],
        name: "dialogue"));
dialogueInput.Activated += activation =>
{
    if (activation.Action == MajoInputActions.Advance)
    {
        AdvanceDialogue();
    }
};
```

For a given action, the newest enabled normal Scope owns delivery. Disposing or
disabling it restores the previous owner. A modal Scope forms a global boundary:
its allowlist is delivered only to that Scope and every other action is blocked
until the modal handle is released. A Muter blocks all or selected actions without
claiming them:

```csharp
using var menuInput = input.AcquireScope(
    menu,
    new InputScopeOptions(
        [MajoInputActions.OptionUp, MajoInputActions.OptionDown,
         MajoInputActions.Confirm, MajoInputActions.Cancel],
        isModal: true,
        name: "menu"));
```

Handles are bound to their Owner. Runtime disposes them on Reset and Shutdown;
handles owned by a Godot `Node` are also released when that Node exits the tree.
One logical action is dispatched at most once per Godot frame, and one physical
event resolves to at most one logical action even when host bindings overlap.
The Godot backend dispatches from `_UnhandledInput`, after GUI Controls have had
the opportunity to consume an event. HUD and menu controls should keep
`MouseFilter = Stop`; non-interactive dialogue surfaces use `Ignore` so clicking
the dialogue panel can still produce `Advance`.

The default Godot backend installs only missing InputMap actions and never changes
an action already defined by the host project:

```text
majo_advance      Space / left mouse / gamepad A
majo_cancel       Escape / right mouse / gamepad B
majo_auto         A / gamepad Y
majo_history      H / gamepad Back
majo_menu         M / gamepad Start
majo_option_up    Up / wheel up / d-pad up
majo_option_down  Down / wheel down / d-pad down
majo_confirm      Enter / middle mouse / gamepad X
```

Set `InputConfiguration.InstallDefaultBindings` to `false` when the host owns the
complete InputMap. `InputConfiguration.Enabled` controls the initial and
post-Reset state; `IInputManager.Enabled` can temporarily suspend routing. Tests
and non-Godot hosts can use `FakeInputBackend`, while projects can replace
`IInputBackend` through `ConfigureServices`.

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
res://assets/audio/bgm/<clip-id>.<audio-extension>
res://assets/audio/sfx/<clip-id>.<audio-extension>
res://assets/audio/voice/<locale>/<line-id>.<audio-extension>
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
var texture = await resources
    .CreateLoader<Texture2D>(configuration.Resources!.CharactersPath)
    .LoadAsync("maya/happy");
```

`ResourceConfiguration` can change the root or category prefixes. A non-empty
`ActorAppearanceConfiguration.TexturePath` is a strict override: Runtime tries
that path only and reports all attempted candidates if it is missing. Custom
resource providers register through `ConfigureResourceProviders`; higher
priority providers can replace project assets without changing logical IDs.

## Audio

`IAudioManager` owns BGM, SFX, and Voice playback without exposing Godot audio
types. BGM uses named tracks and crossfades when a track changes, SFX uses named
instances so the same clip can overlap, and Voice is a single channel where a new
clip stops the previous one:

```csharp
var audio = runtime.Host!.Services.Get<IAudioManager>();
await audio.PlayBgmAsync("courtroom", "music", fade: new AudioFade(0.5));
await audio.PlaySfxAsync("gavel", instanceId: "first-hit");
var voice = await audio.PlayVoiceAsync("line-00042");
await voice.Playback.Completion;
```

Master and per-category volume/mute properties update active players immediately.
Every fade is an `IOperationHandle`; its optional ID and group participate in the
same `await`, `cancel`, and forced `complete` semantics as Actor animation. A
cancelled crossfade restores the old track, while forced completion selects the
new track and releases the old player.

Yarn audio commands use one positional resource ID and named parameters:

```yarn
<<bgm courtroom track:music duration:0.5 operation:music-in>>
<<sfx gavel instance:first-hit>>
<<voice objection wait:true>>
<<stopSfx instance:first-hit duration:0.2 wait:true>>
<<stopBgm track:music duration:0.5 wait:true>>
```

The automatic Voice Presenter first checks line metadata in the exact
`voice:<clip-id>` form, then falls back to the line ID. A metadata override is
strict and fails when the localized resource is missing; `voice:none` disables
voice for that line. Convention-based missing voice is allowed. Replace the
complete manager through `ConfigureServices`, or set `AudioBackendFactory` to use
a custom playback backend. `FakeAudioBackend` and `FakeAudioClipLoader` provide
deterministic tests without an audio device.

## Dialogue sessions

Runtime creates, owns, and releases its Yarn `DialogueRunner`. Game and Presenter
code use the Yarn-free `IDialoguePlayer`, while projects continue to follow the
`res://assets/scripts/<project-id>.yarnproject` resource convention:

```csharp
var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
await dialogue.StartAsync(); // defaults to project "main", node "Start"
await dialogue.Completion;

await dialogue.StartAsync(new DialogueStartRequest("chapter2", "Opening"));
await dialogue.StopAsync();
```

`StartAsync` returns after the session reaches `Running`; `Completion` represents
natural completion or an explicit stop. The state machine is
`Idle -> Loading -> Running -> Stopping -> Idle`, with `Faulted` for project or
command failures. The player publishes resolved start/stop/completion events,
current project/node/line IDs, and the current option phase.

Register Yarn-free Presenters before adding the bootstrap to the scene tree:

```csharp
runtime.ConfigureDialoguePresenters += presenters =>
    presenters.Register(myPresenter);
```

`IDialogueVariables` provides number, string, and boolean values without exposing
Yarn's `$` prefix. The prefix is accepted at the boundary but public change events
publish normalized names. Reset clears the in-memory session variables.

Only integrations that need native Yarn presenters, providers, or diagnostics
should reference `Majo.Runtime.Dialogue.Yarn` and resolve `IYarnDialogueBackend`.
Normal playback and stopping must still go through `IDialoguePlayer` so Runtime can
enforce Command, Operation, Presenter, and Runner cleanup ordering.

## Default dialogue presentation

`MajoProjectConfiguration.CreateDefault()` enables a traditional single-text-box
Presenter. It composes Yarn's native `LinePresenter`, `LetterTypewriter`,
`OptionsPresenter`, `OptionItem`, cancellation tokens, and markup parser; Majo does
not maintain a second typewriter or markup grammar. The first `advance` while text
is appearing requests Yarn HurryUp, and a later frame's `advance` requests
NextLine. Yarn's polling `LineAdvancer` is deliberately not registered.

Presentation settings are centralized under `Presentation`:

```csharp
var configuration = MajoProjectConfiguration.CreateDefault();
configuration.Presentation!.LettersPerSecond = 36;
configuration.Presentation.FontSize = 30;
configuration.Presentation.UseFadeEffect = true;
configuration.Presentation.FadeUpDurationSeconds = 0.15;
configuration.Presentation.Theme = myTheme;
configuration.Presentation.Font = myFont;
configuration.Presentation.PresenterScene = myPresenterScene;
```

A replacement PackedScene must have a `Control` root and descendants named
`LinePresenter`, `OptionsPresenter`, and `OptionContainer` with the corresponding
Yarn/native node types. Runtime configures, registers, resets, and releases those
nodes. Set `Presentation.Enabled = false` when a project supplies a completely
independent Presenter.

Options acquire an M7 modal Scope. Their focus navigation and submission route
through `IInputManager` for keyboard and gamepad. Native `OptionItem` buttons
retain their pressed signal for pointer selection, while unrelated left clicks
are ignored during the modal option phase instead of confirming the focused item.

Yarn markup is projected into immutable `DialogueText` and
`DialogueMarkupAttribute` values. The default Presenter maps Yarn's `[b]`, `[i]`,
`[u]`, and `[s]` spans to `RichTextLabel` BBCode while preserving visible-character
positions for the native typewriter. Register ordinary handlers without importing
Yarn types:

```csharp
runtime.ConfigureDialogueMarkupHandlers += handlers =>
    handlers.Register(myMarkupHandler);
```

Handlers execute sequentially in registration order and receive the original
Yarn typewriter cancellation token for per-character asynchronous work. Unknown
attributes and their typed properties remain available in the facade. Native Yarn
handlers remain an advanced integration under `Majo.Runtime.Dialogue.Yarn`.

`IDialoguePresenter` remains the Yarn-free extension boundary for observers and
game-specific Presenters. `IDialoguePresentation` exposes only the default view's
line-complete and option-phase state for later Auto/History composition.

## Auto and Session History

Runtime registers Yarn-free `IAutoManager` and `IHistoryManager` services. Auto
starts its unscaled deadline only after the default Presenter completes the line
and `IScriptPlayback` reaches `AwaitingAdvance`. It can optionally wait for the
current Voice playback before applying the configured extra delay:

```csharp
var configuration = MajoProjectConfiguration.CreateDefault();
configuration.Auto!.WaitForVoice = true;
configuration.Auto.ExtraDelaySeconds = 1;
configuration.History!.Capacity = 100;
configuration.History.ShowDefaultUi = true;

var auto = runtime.Host!.Services.Get<IAutoManager>();
auto.SetMode(AutoMode.Auto);

var history = runtime.Host.Services.Get<IHistoryManager>();
history.Open();
await history.ReplayVoiceAsync(history.Entries[0].Sequence);
```

Options and the History modal pause Auto and closing them restarts a full
deadline. Manual Advance cancels the pending deadline; the Yarn backend also
deduplicates same-boundary NextLine requests so a manual/deadline race advances
exactly once. The default bindings are `A` for Auto and `H` for History.

History records only `LineDisplayCompleted` boundaries, retaining the stable line
ID, speaker, final visible text, and resolved Voice ID. Capacity eviction is FIFO
and duplicate line IDs remain separate ordered entries. The built-in overlay
lists entries and enables Voice replay only when a localized clip exists.

History is registered as a `Session` State contributor. Runtime Reset closes and
clears it, returns Auto to Manual, and rebinds both global input scopes after the
Input service resets. History is deliberately excluded from Game snapshots and
future disk saves.

## Durable State foundation

M12 separates state capture, canonical serialization, storage, and destructive
restore. It intentionally does not provide save slots or Save/Load UI; those
product behaviors compose these services in M13.

```csharp
var state = runtime.Host!.Services.Get<IStateManager>();
var eligibility = runtime.Host.Services.Get<IStateEligibility>();
var serializer = runtime.Host.Services.Get<IStateSerializer>();
var storage = runtime.Host.Services.Get<IStateStorage>();

if (eligibility.Check(StateOperationKind.Save).IsAllowed)
{
    var snapshot = await state.CaptureGameAsync(); // validates eligibility again
    var envelope = state.CreateEnvelope(
        snapshot,
        StateScope.Game,
        MajoRuntimeMetadata.PackageVersion,
        configuration.State!.GameBuildId,
        configuration.State.StorageNamespace);
    await storage.WriteAsync("manual/example", serializer.Serialize(envelope));
}
```

Game capture is allowed only at `AwaitingAdvance` with no active Command,
Operation, modal input owner, state transaction, or host barrier. A host gameplay
system can acquire an `IStateBarrierHandle` with a stable reason code and separate
Save/Restore/Rollback mask. Checks are read-only and Capture/Restore always check
again to close time-of-check/time-of-use races.

Every `IStateContributor` declares a `StateContributorCapability`:
`SupportsPersistence`, an independent `RollbackPolicy`, and whether a missing old
record is `Required` or may `UseDefault`. Third-party contributors are transient
by default. Register key/schema migrations and explicitly removed keys through
`ConfigureStateMigrations`; unknown records and missing required records are never
silently ignored.

`CanonicalJsonStateSerializer` produces deterministic UTF-8 JSON with a SHA-256
integrity checksum. `GodotUserStateStorage` writes beneath the validated
`user://<StorageNamespace>` logical namespace using a flushed same-directory
temporary file and atomic replacement. Cancellation is observed only before the
commit point; after replacement the call reports success. Replace either default
through `ConfigureServices.ReplaceSingleton<IStateSerializer>` or
`ReplaceSingleton<IStateStorage>`.

`IGameSessionCoordinator.LoadGameAsync` and `RollbackAsync` share
`IStateRestoreTransaction` preflight and destructive execution. Load begins a new
Session, clears History, and returns playback to Manual; Rollback preserves the
Session and History. `IPlaybackModeManager` is the single Manual/Auto owner, while
`IAutoManager` remains a compatibility facade with no second input scope,
deadline, or pause state.

## Yarn commands

During initialization Runtime registers every entry in the immutable command
catalog on its owned Runner and removes them in reverse order during shutdown.

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
exact command usage. `CommandDescriptor`, `CommandBinder`, and
`RuntimeCommandDispatcher` are independent of Yarn; the adapter only translates
catalog entries into Yarn handlers.

Custom Yarn commands register before initialization without changing Runtime
core source:

```csharp
[RuntimeCommand("setCaseMode", Category = "Game")]
public sealed class SetCaseModeArguments
{
    [CommandParameter("mode", 0, Primary = true, Required = true)]
    public string Mode { get; set; } = string.Empty;
}

runtime.ConfigureCommands = commands =>
    commands.Register<SetCaseModeArguments>(async (arguments, context) =>
    {
        var services = context.Require(context.Services, "ServiceResolver");
        services.Get<CaseService>().SetMode(arguments.Mode);
        await Task.CompletedTask;
    });
```

Command argument classes own their names, usage metadata, defaults and
constraints. The shared binder provides typed conversion and uniform errors;
adding a command does not modify the Yarn adapter or a central parser. Register
additional value types through `ConfigureCommandConverters` before startup.
Related command families can implement `IRuntimeCommandModule` and register
atomically with `RuntimeCommandRegistry.RegisterModule`.
Keep each command's argument schema and handler in the same file; module files
should contain registration only. The built-in commands under
`Runtime/Dialogue/Commands/BuiltIn` follow this vertical-slice layout.

Every handler receives a `RuntimeCommandContext`. Operations tracked through
the context are cancelled when the current Yarn dialogue stops. Command
failures emit `RuntimeCommandFailed`, abort the current dialogue and remain
available through `IYarnCommandAdapter.LastFailure`.

## Runtime validation demo

Run the project or open `res://demo/RuntimeDemo.tscn`. This is the single,
long-lived validation scene; new Runtime capabilities are appended here instead
of creating milestone-named demos. Its current long-form Yarn flow covers service
lifecycle, resources, parallel/wait/cancel/complete Operations, Actors, built-in
and custom commands, dialogue sessions and variables, input, native markup,
BGM/SFX/Voice, Script playback, Game State capture/restore, Auto, and Session
History.

The Dialogue Panel is hosted on CanvasLayer 20, above Actor layers. The validation
HUD is on layer 30 and History is modal on layer 40. Use Space/mouse for dialogue,
arrow keys/Enter for options, `A` for Auto, and `H` for History. At a completed
line boundary the HUD also enables Game State capture and restore buttons.

All extension code lives outside the plugin under `res://demo`:

- `DemoSessionService` demonstrates a service with Runtime lifetime dependency.
- `DemoPortraitActorFactory` demonstrates a custom Actor factory ID.
- `assets/audio` contains the Demo's loopable ambient BGM, soft notification SFX,
  and localized Narrator/Maya Voice assets. Headless and `--demo-auto` runs
  substitute `FakeAudioClipLoader`/`FakeAudioBackend` so validation does not
  depend on an audio device or its shutdown timing.
- `demo_route` demonstrates a custom Yarn command resolving the service.
- `RuntimeDemoHud` exposes playback, State, Auto, and History controls.
- `RuntimeDialogueProbe` runs the same content without graphics for deterministic
  integration tests.

The main scene also has a non-interactive smoke mode:

```sh
/path/to/godot --headless --path game -- --demo-auto
```

## Versioning strategy & contracts

- **Runtime API**: Semantic Versioning (`0.1.0` baseline, stable public contracts across services, descriptors, and commands).
- **State checkpoints**: `ScriptCheckpoint` format version `1`. Checkpoint restoration uses SHA-256 `ProgramFingerprint` and stable Yarn line IDs. Checkpoint restoration is strictly validated before mutating active runtime state and never guesses line positions.
- **Yarn dependency baseline**: Locked to `YarnSpinner-Godot 0.3.22` and `Yarn Spinner 3.2.0`. The internal VM bridge accesses Yarn internals via `[InternalsVisibleTo("YarnSpinner.Tests")]` and validates runtime memory layout on bootstrap initialization, failing fast if an incompatible version is installed.

## Troubleshooting

- **Build error: CS0246: The type or namespace name 'YarnBridge' / 'YarnSpinnerGodot' could not be found**:
  Ensure both `<Import Project="addons\YarnSpinner-Godot\YarnSpinner-Godot.props" />` and `<Import Project="addons\majo_runtime\MajoRuntime.props" />` are present in your game `.csproj`.
- **RuntimeLifecycleException: Yarn dependency check failed**:
  Verify `addons/YarnSpinner-Godot` is installed and has version `0.3.22` in `plugin.cfg` and `3.2.0` in `YarnSpinner-Godot.props`.
- **InvalidOperationException: Yarn VM state cannot be captured**:
  State snapshots can only be captured at safe `AwaitingAdvance` line boundaries with empty VM operand and call stacks.
- **ResourceNotFoundException: Resource 'scripts/<project-id>' was not found**:
  Ensure your `.yarnproject` file is located under `res://assets/scripts/<project-id>.yarnproject` and has been imported by Godot.

## Testing

Run the complete local gate from the repository root:

```sh
./tools/test.sh
```

The gate performs:
1. Scoped C# formatting and static diagnostics across all projects (`game`, unit tests, and clean-consumer fixture).
2. Plugin distribution staging and file cleanliness verification (no test/demo/build-cache leakage or repository-specific paths).
3. Non-incremental C# build of the main game project.
4. Pure C# xUnit unit tests.
5. Clean-consumer fixture verification from a fresh staged installation (no external bridge references, full round-trip execution, typed missing/incompatible dependency probes, and negative assertion probe).
6. Godot headless import and C# compilation for the main project.
7. Visible Runtime Demo headless smoke test (`--demo-auto`).
8. Three consecutive Godot headless integration test runs.
9. Negative probe verification (confirming failed assertions return non-zero exit codes).
10. Release build configuration smoke tests for both projects and a real Godot export smoke for the host target platform.

Set `GODOT_BIN` when Godot Mono is installed outside the default macOS path:

```sh
GODOT_BIN=/path/to/godot ./tools/test.sh
```

The host-platform Godot 4.7.2 Mono export templates must be installed for the
final export smoke. A missing template is a gate failure, not a skipped check.

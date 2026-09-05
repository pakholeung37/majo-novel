#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Localization;
using Majo.Runtime.Plugins;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.Stage;
using Majo.Runtime.State;
using Majo.Runtime.UI.Diagnostics;
using Majo.YarnBridge;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    [Signal]
    public delegate void RuntimeInitializedEventHandler();

    [Signal]
    public delegate void RuntimeInitializationFailedEventHandler(string message);

    [Signal]
    public delegate void RuntimeShutdownEventHandler();

    [Signal]
    public delegate void RuntimeCommandFailedEventHandler(string commandName, string message);

    private readonly TaskCompletionSource initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource bootstrapLifetime = new();
    private Task? shutdownTask;
    private readonly DelegatingYarnCommandAdapter yarnCommandAdapter = new();
    private YarnDialogueBackend? yarnDialogueBackend;
    private DialoguePlayer? dialoguePlayer;
    private YarnDefaultDialoguePresentation? dialoguePresentation;
    private ISaveProductManager? saveProductManager;
    private bool isExitingTree;
    private bool isBootstrapLifetimeDisposed;
    private bool isRuntimeShutdownSignalEmitted;
    private readonly PluginManager pluginManager = new();
    private readonly RuntimeCommandRegistry commandRegistry = new();
    private readonly CommandValueConverterRegistry commandConverters = new();
    private readonly CommandParameterGroupRegistry commandParameterGroups = new();
    private readonly TransitionDriverRegistry transitionDrivers = new();
    private readonly CameraModifierRegistry cameraModifiers = new();
    private readonly TransientCueHandlerRegistry transientCueHandlers = new();
    private readonly PluginRegistrationContext pluginRegistration;

    public MajoRuntimeBootstrap()
    {
        pluginRegistration = new PluginRegistrationContext(
            commandRegistry,
            transitionDrivers,
            cameraModifiers,
            transientCueHandlers,
            commandConverters,
            commandParameterGroups);
    }

    public IPluginManager Plugins => pluginManager;

    public Action<ITransitionDriverRegistry>? ConfigureTransitionDrivers { get; set; }

    public Action<ICameraModifierRegistry>? ConfigureCameraModifiers { get; set; }

    public MajoRuntimeBootstrap AddPlugin(IMajoPlugin plugin)
    {
        pluginManager.Register(plugin);
        return this;
    }

    [Export]
    public MajoProjectConfiguration? Configuration { get; set; }

    public Action<RuntimeServiceRegistry>? ConfigureServices { get; set; }

    public Action<DiagnosticContributorRegistry>? ConfigureDiagnosticContributors { get; set; }

    public Action<ActorManager>? ConfigureActorFactories { get; set; }

    public Action<RuntimeCommandRegistry>? ConfigureCommands { get; set; }

    public Action<CommandValueConverterRegistry>? ConfigureCommandConverters { get; set; }

    public Action<CommandParameterGroupRegistry>? ConfigureCommandParameterGroups { get; set; }

    public Action<ResourceProviderRegistry>? ConfigureResourceProviders { get; set; }

    public IResourcePathResolver? ResourcePathResolver { get; set; }

    public IDirectionResourceKeyResolver? DirectionResourceResolver { get; set; }

    public IDirectionPositionResolver? DirectionPositionResolver { get; set; }

    public Action<DialoguePresenterRegistry>? ConfigureDialoguePresenters { get; set; }

    public Action<DialogueMarkupHandlerRegistry>? ConfigureDialogueMarkupHandlers { get; set; }

    public DialogueSpeakerNameResolver? ResolveSpeakerName { get; set; }

    public Action<ILocaleManager>? ConfigureLocales { get; set; }

    public Action<StateContributorRegistry>? ConfigureStateContributors { get; set; }

    public Action<StateMigrationRegistry>? ConfigureStateMigrations { get; set; }

    public Action<StageTransitionProviderRegistry>? ConfigureStageTransitionProviders { get; set; }

    public Func<Node, IAudioBackend>? AudioBackendFactory { get; set; }

    public StructuredRuntimeLogger? Logger { get; private set; }

    public RingBufferLogSink? RingBuffer { get; private set; }

    public DefaultDebugOverlay? DebugOverlay { get; private set; }

    public DefaultFpsOverlay? FpsOverlay { get; private set; }

    public RuntimeHost? Host { get; private set; }

    public IRuntimeDiagnostics? Diagnostics =>
        Host?.Services.TryGet<IRuntimeDiagnostics>(out var d) == true ? d : null;

    public bool IsInitialized => Host?.State == RuntimeHostState.Initialized;

    public Task InitializationCompletion => initializationCompletion.Task;

    public Task ShutdownCompletion => shutdownCompletion.Task;

    public YarnDependencyStatus DependencyStatus { get; private set; } =
        YarnDependencyStatus.NotChecked;

    public override void _Ready()
    {
        _ = InitializeRuntimeAsync();
    }

    public override void _ExitTree()
    {
        isExitingTree = true;
        CancelBootstrapLifetime();
        EmitRuntimeShutdownOnce();
        shutdownTask ??= ShutdownHostAsync();
    }

    public async Task ResetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        if (Host is null)
        {
            throw new RuntimeLifecycleException("Majo Runtime has not created a host.");
        }

        await ExecuteWithLifecycleTimeoutAsync(
            token => Host.ResetAsync(token),
            "reset",
            cancellationToken);
        Logger?.SetRootCorrelation(new RuntimeLogCorrelation(
            Host.RuntimeId,
            Host.SessionId));
    }

    public async Task RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        CancelBootstrapLifetime();
        shutdownTask ??= ShutdownHostAsync(cancellationToken);
        await shutdownTask;
        await shutdownCompletion.Task;

        if (IsInsideTree())
        {
            QueueFree();
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        try
        {
            DependencyStatus = YarnDependencyValidator.ValidateProject(
                ProjectSettings.GlobalizePath("res://"));

            if (!DependencyStatus.IsCompatible)
            {
                throw new RuntimeLifecycleException(
                    $"Yarn dependency check failed: {DependencyStatus.Message}");
            }

            YarnVmStateBridge.ValidateRuntimeLayout();

            if (Configuration is null)
            {
                throw new MajoConfigurationException(new[]
                {
                    new ConfigurationIssue(nameof(Configuration), "Configuration is required."),
                });
            }

            var validation = Configuration.Validate();
            validation.ThrowIfInvalid();

            InitializeLogging();

            ConfigureAndSealPlugins();

            var services = new RuntimeServiceRegistry();
            RegisterDefaultServices(services);
            // Plugins configure against the complete built-in graph. This makes
            // ReplaceSingleton useful while still keeping every registration
            // before the graph is sealed and instantiated.
            pluginManager.ConfigureServices(services);
            ConfigureServices?.Invoke(services);
            Host = services.Build(Logger);
            Logger?.SetRootCorrelation(new RuntimeLogCorrelation(
                Host.RuntimeId,
                Host.SessionId));

            await ExecuteWithLifecycleTimeoutAsync(
                token => Host.InitializeAsync(token),
                "initialization",
                bootstrapLifetime.Token);

            var backend = Host.Services.Get<IYarnDialogueBackend>();
            var innerAdapter = CreateYarnCommandAdapter(
                backend,
                Host.Services);
            await innerAdapter.InitializeAsync(bootstrapLifetime.Token);
            yarnCommandAdapter.Attach(innerAdapter);
            yarnCommandAdapter.CommandFailed += HandleCommandFailed;

            var pluginContext = new PluginContext(
                Host.Services,
                Configuration,
                Logger!);
            await pluginManager.InitializeAllAsync(pluginContext, bootstrapLifetime.Token);

            InitializeOverlays();

            if (isExitingTree)
            {
                throw new OperationCanceledException(
                    "Majo Runtime node exited during initialization.",
                    bootstrapLifetime.Token);
            }

            initializationCompletion.TrySetResult();
            EmitSignal(SignalName.RuntimeInitialized);
        }
        catch (Exception exception)
        {
            initializationCompletion.TrySetException(exception);
            _ = initializationCompletion.Task.Exception;
            if (!isExitingTree)
            {
                GD.PushError($"Majo Runtime initialization failed: {exception}");
                EmitSignal(SignalName.RuntimeInitializationFailed, exception.Message);
                QueueFree();
            }
        }
    }

    private void ConfigureAndSealPlugins()
    {
        BuiltInRuntimeCommands.Register(commandRegistry);
        new AudioCommandModule().Register(commandRegistry);
        new DirectionCommandModule().Register(commandRegistry);

        ConfigureTransitionDrivers?.Invoke(transitionDrivers);
        ConfigureCameraModifiers?.Invoke(cameraModifiers);
        ConfigureCommands?.Invoke(commandRegistry);
        ConfigureCommandConverters?.Invoke(commandConverters);
        ConfigureCommandParameterGroups?.Invoke(commandParameterGroups);

        pluginManager.Configure(pluginRegistration);

        commandRegistry.SealRegistrations();
        commandConverters.Seal();
        commandParameterGroups.Seal();
        transitionDrivers.Seal();
        cameraModifiers.Seal();
        transientCueHandlers.Seal();
        pluginManager.SealRegistration();
    }
}

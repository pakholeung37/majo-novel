#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Diagnostics.Godot;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Input.Godot;
using Majo.Runtime.History;
using Majo.Runtime.History.Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.YarnBridge;
using YarnSpinnerGodot;

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
    private IYarnCommandAdapter? yarnCommandAdapter;
    private bool isExitingTree;
    private bool isBootstrapLifetimeDisposed;
    private bool isRuntimeShutdownSignalEmitted;

    [Export]
    public MajoProjectConfiguration? Configuration { get; set; }

    public Action<RuntimeServiceRegistry>? ConfigureServices { get; set; }

    public Action<DiagnosticContributorRegistry>? ConfigureDiagnosticContributors { get; set; }

    public Action<ActorManager>? ConfigureActorFactories { get; set; }

    public Action<RuntimeCommandRegistry>? ConfigureCommands { get; set; }

    public Action<CommandValueConverterRegistry>? ConfigureCommandConverters { get; set; }

    public Action<ResourceProviderRegistry>? ConfigureResourceProviders { get; set; }

    public Action<DialoguePresenterRegistry>? ConfigureDialoguePresenters { get; set; }

    public Action<DialogueMarkupHandlerRegistry>? ConfigureDialogueMarkupHandlers { get; set; }

    public Action<StateContributorRegistry>? ConfigureStateContributors { get; set; }

    public Action<StateMigrationRegistry>? ConfigureStateMigrations { get; set; }

    public Func<Node, IAudioBackend>? AudioBackendFactory { get; set; }

    public StructuredRuntimeLogger? Logger { get; private set; }

    public RingBufferLogSink? RingBuffer { get; private set; }

    public DefaultDebugOverlay? DebugOverlay { get; private set; }

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
            cancellationToken,
            "reset");
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

            var sinks = new List<IRuntimeLogSink>();
            var ringCapacity = Configuration.Diagnostics?.RingBufferCapacity ?? 500;
            RingBuffer = new RingBufferLogSink(ringCapacity);
            sinks.Add(RingBuffer);
            sinks.Add(new GodotConsoleSink());

            if (Configuration.Diagnostics?.EnableFileLogging == true)
            {
                var rawDir = Configuration.Diagnostics.FileLogDirectory;
                var logDir = rawDir.StartsWith("user://", StringComparison.Ordinal) || rawDir.StartsWith("res://", StringComparison.Ordinal)
                    ? ProjectSettings.GlobalizePath(rawDir)
                    : rawDir;
                sinks.Add(new JsonLinesFileSink(
                    logDir,
                    "majo_runtime",
                    Configuration.Diagnostics.MaxFileSizeBytes,
                    Configuration.Diagnostics.MaxFileCount));
            }

            var compositeSink = new CompositeRuntimeLogSink(sinks);
            var minLevel = Configuration.Diagnostics?.MinimumLevel ?? RuntimeLogLevel.Information;
            Logger = new StructuredRuntimeLogger(compositeSink, minLevel);

            var services = new RuntimeServiceRegistry();
            RegisterDefaultServices(services);
            ConfigureServices?.Invoke(services);
            Host = services.Build(Logger);
            Logger.SetRootCorrelation(new RuntimeLogCorrelation(
                Host.RuntimeId,
                Host.SessionId));
            await ExecuteWithLifecycleTimeoutAsync(
                token => Host.InitializeAsync(token),
                bootstrapLifetime.Token,
                "initialization");

            if (Host.Services.TryGet<IYarnCommandAdapter>(out var adapter))
            {
                yarnCommandAdapter = adapter!;
                adapter!.CommandFailed += HandleCommandFailed;
            }

            if (Configuration.Diagnostics?.EnableOverlay == true &&
                Host.Services.TryGet<IRuntimeDiagnostics>(out var diagnostics) &&
                Host.Services.TryGet<IInputManager>(out var inputManager))
            {
                DebugOverlay = new DefaultDebugOverlay(this, diagnostics!, inputManager!, Configuration.Diagnostics);
            }

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

    private void RegisterDefaultServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<IRuntimeDiagnostics>(
                resolver =>
                {
                    var contributors = new DiagnosticContributorRegistry();
                    ConfigureDiagnosticContributors?.Invoke(contributors);
                    return new RuntimeDiagnosticsService(
                        () => Host?.Services ?? resolver,
                        RingBuffer,
                        contributors,
                        Host?.RuntimeId);
                })
            .AddSingleton<IInputBackend>(
                _ => new GodotInputBackend(this, Configuration!.Input!))
            .AddSingleton<IInputManager>(
                resolver => new InputManager(
                    resolver.Get<IInputBackend>(),
                    Configuration!.Input!.Enabled,
                    Logger),
                typeof(IInputBackend))
            .AddSingleton<IFrameClock>(_ => new GodotFrameClock(GetTree()))
            .AddSingleton<IMissingResourceReporter>(_ => new MissingResourceReporter(Logger))
            .AddSingleton<IResourceManager>(
                resolver => CreateResourceManager(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IActorTextureLoader>(
                resolver => new ResourceActorTextureLoader(
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.FailOnMissingAssets,
                    resolver.Get<IMissingResourceReporter>()),
                typeof(IResourceManager),
                typeof(IMissingResourceReporter))
            .AddSingleton<IOperationScheduler>(
                resolver => new OperationScheduler(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>(),
                    Logger),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IAudioManager>(
                resolver => CreateAudioManager(
                    resolver.Get<IResourceManager>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IMissingResourceReporter>()),
                typeof(IResourceManager),
                typeof(IOperationScheduler),
                typeof(IMissingResourceReporter))
            .AddSingleton<GodotActorHost>(_ => new GodotActorHost(this))
            .AddSingleton<IActorManager>(
                resolver => CreateActorManager(
                    resolver.Get<GodotActorHost>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IActorTextureLoader>()),
                typeof(GodotActorHost),
                typeof(IOperationScheduler),
                typeof(IActorTextureLoader));

        var presenterRegistry = new DialoguePresenterRegistry();
        ConfigureDialoguePresenters?.Invoke(presenterRegistry);
        var markupHandlerRegistry = new DialogueMarkupHandlerRegistry();
        ConfigureDialogueMarkupHandlers?.Invoke(markupHandlerRegistry);
        services
            .AddSingleton<IYarnDialogueBackend>(
                resolver => new YarnDialogueBackend(
                    this,
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.ScriptsPath,
                    presenterRegistry.Presenters
                        .Concat<IDialoguePresenter>(
                            [new VoiceDialoguePresenter(resolver.Get<IAudioManager>())])
                        .ToArray()),
                typeof(IResourceManager),
                typeof(IAudioManager))
            .AddSingleton<IDialoguePresentation>(
                resolver => new YarnDefaultDialoguePresentation(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IFrameClock>(),
                    Configuration!.Presentation!,
                    markupHandlerRegistry.Handlers),
                typeof(IYarnDialogueBackend),
                typeof(IInputManager),
                typeof(IFrameClock))
            .AddSingleton<IDialogueVariables>(
                resolver => new DialogueVariables(
                    resolver.Get<IYarnDialogueBackend>().VariableStore),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IYarnCommandAdapter>(
                resolver => CreateYarnCommandAdapter(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IActorManager>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IAudioManager>()),
                typeof(IYarnDialogueBackend),
                typeof(IActorManager),
                typeof(IOperationScheduler),
                typeof(IAudioManager))
            .AddSingleton<IDialoguePlayer>(
                resolver => new DialoguePlayer(
                    resolver.Get<IYarnDialogueBackend>(),
                    Configuration!.Dialogue!.DefaultProjectId,
                    Configuration.Dialogue.DefaultStartNode,
                    Logger),
                typeof(IYarnDialogueBackend),
                typeof(IYarnCommandAdapter),
                typeof(IDialoguePresentation))
            .AddSingleton<IScriptManager>(
                resolver => new ScriptManager(resolver.Get<IYarnDialogueBackend>()),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IScriptPlayback>(
                resolver => new ScriptPlayback(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IYarnDialogueBackend>(),
                    Logger),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IHistoryManager>(
                resolver => new HistoryManager(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IInputManager>(),
                    Configuration!.History!.Capacity,
                    Logger),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IAudioManager),
                typeof(IInputManager))
            .AddSingleton<IPlaybackModeManager>(
                resolver => new PlaybackModeManager(
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IHistoryManager>(),
                    TimeSpan.FromSeconds(Configuration!.Auto!.ExtraDelaySeconds),
                    Configuration.Auto.WaitForVoice,
                    Logger),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IAudioManager),
                typeof(IFrameClock),
                typeof(IInputManager),
                typeof(IHistoryManager))
            .AddSingleton<IAutoManager>(
                resolver => new AutoManager(resolver.Get<IPlaybackModeManager>()),
                typeof(IPlaybackModeManager));

        if (Configuration!.History!.ShowDefaultUi)
        {
            services.AddSingleton<DefaultHistoryOverlay>(
                resolver => new DefaultHistoryOverlay(
                    this,
                    resolver.Get<IHistoryManager>()),
                typeof(IHistoryManager));
        }

        if (Configuration!.State!.Enabled)
        {
            var stateMigrations = new StateMigrationRegistry();
            ConfigureStateMigrations?.Invoke(stateMigrations);
            services.AddSingleton<IStateSerializer>(
                _ => new CanonicalJsonStateSerializer(stateMigrations));
            services.AddSingleton<IStateStorage>(
                _ => new GodotUserStateStorage(Configuration.State!.StorageNamespace));
            services.AddSingleton<IStateEligibility>(
                resolver => new StateEligibility(
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IYarnCommandAdapter>()),
                typeof(IScriptPlayback),
                typeof(IOperationScheduler),
                typeof(IInputManager),
                typeof(IYarnCommandAdapter));
            services.AddSingleton<IStateManager>(
                resolver => CreateStateManager(
                    resolver.Get<IDialogueVariables>(),
                    resolver.Get<IActorManager>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IHistoryManager>(),
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStateEligibility>(),
                    stateMigrations),
                typeof(IDialogueVariables),
                typeof(IActorManager),
                typeof(IAudioManager),
                typeof(IHistoryManager),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IInputManager),
                typeof(IOperationScheduler),
                typeof(IStateEligibility));
            services.AddSingleton<IGameSessionCoordinator>(
                resolver => new GameSessionCoordinator(
                    resolver.Get<IRuntimeSessionController>(),
                    resolver.Get<IStateManager>(),
                    resolver.Get<IHistoryManager>(),
                    resolver.Get<IPlaybackModeManager>(),
                    resolver.Get<IStateEligibility>()),
                typeof(IRuntimeSessionController),
                typeof(IStateManager),
                typeof(IHistoryManager),
                typeof(IPlaybackModeManager),
                typeof(IStateEligibility));
        }
    }

    private StateManager CreateStateManager(
        IDialogueVariables variables,
        IActorManager actors,
        IAudioManager audio,
        IHistoryManager history,
        IScriptPlayback scriptPlayback,
        IDialoguePlayer dialogue,
        IInputManager input,
        IOperationScheduler operations,
        IStateEligibility eligibility,
        StateMigrationRegistry migrations)
    {
        var contributors = new StateContributorRegistry()
            .Register(new DialogueVariablesStateContributor(variables))
            .Register(new ActorStateContributor(actors))
            .Register(new AudioStateContributor(audio))
            .Register(new SessionHistoryStateContributor(history));
        ConfigureStateContributors?.Invoke(contributors);
        contributors.Register(new ScriptStateContributor(scriptPlayback));
        return new StateManager(
            contributors.Build(),
            dialogue,
            input,
            operations,
            eligibility,
            migrations,
            Logger);
    }

    private IYarnCommandAdapter CreateYarnCommandAdapter(
        IYarnDialogueBackend backend,
        IActorManager actors,
        IOperationScheduler operations,
        IAudioManager audio)
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands, new ActorCommandService(actors));
        commands.RegisterModule(new AudioCommandModule(audio));
        ConfigureCommands?.Invoke(commands);
        var catalog = commands.BuildCatalog();
        var converters = new CommandValueConverterRegistry();
        ConfigureCommandConverters?.Invoke(converters);
        var binder = new CommandBinder(
            new CommandBindingContext(
                Configuration!.Operations!.DefaultAnimationDurationSeconds,
                Configuration.Audio!.DefaultFadeDurationSeconds),
            converters);
        var dispatcher = new RuntimeCommandDispatcher(
            binder,
            actors,
            operations,
            Host!.Services,
            Logger);
        var adapter = new YarnCommandAdapter(
            new DialogueRunnerCommandRegistry(backend.Runner),
            catalog,
            dispatcher);
        backend.ObserveCommandAdapter(adapter);
        return adapter;
    }

    private ActorManager CreateActorManager(
        GodotActorHost host,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader)
    {
        var actorConfiguration = Configuration!.Actors!;
        var manager = new ActorManager(
            ActorConfigurationCompiler.Compile(actorConfiguration, Configuration.Resources!),
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    host.CharacterLayer,
                    scheduler,
                    textureLoader,
                    new CharacterPositionMap(actorConfiguration)),
                new BackgroundActorFactory(
                    host.BackgroundLayer,
                    scheduler,
                    textureLoader),
            });
        ConfigureActorFactories?.Invoke(manager);
        return manager;
    }

    private IResourceManager CreateResourceManager(
        IFrameClock frameClock,
        IRuntimeLifetime lifetime)
    {
        var godotProvider = new GodotProjectResourceProvider(
                Configuration!.Resources!.RootPath,
                frameClock)
            .RegisterSourceExtensions<YarnProject>(YarnProject.YARN_PROJECT_EXTENSION)
            .RegisterSourceExtensions<AudioStream>("wav", "ogg", "mp3");
        var providers = new ResourceProviderRegistry().Register(godotProvider);
        ConfigureResourceProviders?.Invoke(providers);
        return new ResourceManager(providers.Providers, lifetime, Logger);
    }

    private IAudioManager CreateAudioManager(
        IResourceManager resources,
        IOperationScheduler operations,
        IMissingResourceReporter missingResources)
    {
        var configuration = Configuration!.Audio!;
        return new AudioManager(
            AudioBackendFactory?.Invoke(this) ?? new GodotAudioBackend(this),
            operations,
            new GodotAudioClipLoader(resources, configuration.BgmPath),
            new GodotAudioClipLoader(resources, configuration.SfxPath),
            new GodotAudioClipLoader(resources, configuration.VoicePath),
            new AudioSettings(
                configuration.MasterVolume,
                configuration.BgmVolume,
                configuration.SfxVolume,
                configuration.VoiceVolume,
                configuration.MasterMuted,
                configuration.BgmMuted,
                configuration.SfxMuted,
                configuration.VoiceMuted,
                configuration.VoiceLocale,
                configuration.FailOnMissingClips),
            Logger,
            missingResources);
    }

    private async Task ShutdownHostAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (yarnCommandAdapter is not null)
            {
                yarnCommandAdapter.CommandFailed -= HandleCommandFailed;
                yarnCommandAdapter = null;
            }

            if (Host is not null)
            {
                await ExecuteWithLifecycleTimeoutAsync(
                    token => Host.ShutdownAsync(token),
                    cancellationToken,
                    "shutdown");
            }

            DebugOverlay?.Dispose();
            DebugOverlay = null;
            Logger?.Dispose();
            Logger = null;

            shutdownCompletion.TrySetResult();
            if (!isExitingTree && IsInsideTree())
            {
                EmitRuntimeShutdownOnce();
            }
        }
        catch (Exception exception)
        {
            shutdownCompletion.TrySetException(exception);
            _ = shutdownCompletion.Task.Exception;
            GD.PushError($"Majo Runtime shutdown failed: {exception}");
        }
        finally
        {
            if (!isBootstrapLifetimeDisposed)
            {
                bootstrapLifetime.Dispose();
                isBootstrapLifetimeDisposed = true;
            }
        }
    }

    private void HandleCommandFailed(RuntimeCommandException failure) => EmitSignal(
        SignalName.RuntimeCommandFailed,
        failure.CommandName,
        failure.Message);

    private void EmitRuntimeShutdownOnce()
    {
        if (isRuntimeShutdownSignalEmitted || Host is null)
        {
            return;
        }

        isRuntimeShutdownSignalEmitted = true;
        EmitSignal(SignalName.RuntimeShutdown);
    }

    private void CancelBootstrapLifetime()
    {
        if (!isBootstrapLifetimeDisposed && !bootstrapLifetime.IsCancellationRequested)
        {
            bootstrapLifetime.Cancel();
        }
    }

    private async Task ExecuteWithLifecycleTimeoutAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string operationName)
    {
        var timeoutSeconds = Configuration?.Runtime?.LifecycleTimeoutSeconds ?? 10;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task operationTask;

        try
        {
            operationTask = operation(operationCancellation.Token);
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        using var deadlineCancellation = new CancellationTokenSource();
        var cancellationSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationSignal.TrySetResult());
        var deadlineTask = Task.Delay(timeout, deadlineCancellation.Token);
        var completedTask = await Task.WhenAny(
            operationTask,
            deadlineTask,
            cancellationSignal.Task);

        if (completedTask == operationTask)
        {
            deadlineCancellation.Cancel();
            try
            {
                await operationTask;
            }
            finally
            {
                operationCancellation.Dispose();
            }

            return;
        }

        var cancellationException = TryCancel(operationCancellation);
        deadlineCancellation.Cancel();
        _ = ObserveTimedOutOperationAsync(operationTask, operationCancellation);

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var message =
            $"Majo Runtime {operationName} exceeded {timeoutSeconds:0.###} seconds.";
        throw cancellationException is null
            ? new TimeoutException(message)
            : new TimeoutException(message, cancellationException);
    }

    private static Exception? TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task ObserveTimedOutOperationAsync(
        Task operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

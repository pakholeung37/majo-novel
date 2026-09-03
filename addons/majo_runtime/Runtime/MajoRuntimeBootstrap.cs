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
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Input.Godot;
using Majo.Runtime.History;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Played;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
using Majo.Runtime.Script;
using Majo.Runtime.Save;
using Majo.Runtime.Save.Godot;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.Stage;
using Majo.Runtime.Stage.Godot;
using Majo.Runtime.UI.Diagnostics;
using Majo.Runtime.UI.History;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;
using Majo.Runtime.Travel;
using Majo.Runtime.UI.Travel;
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
    private YarnDialogueBackend? yarnDialogueBackend;
    private DialoguePlayer? dialoguePlayer;
    private YarnDefaultDialoguePresentation? dialoguePresentation;
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

            var sinks = new List<IRuntimeLogSink>();
            var ringCapacity = Configuration.Diagnostics?.RingBufferCapacity ?? 500;
            RingBuffer = new RingBufferLogSink(ringCapacity);
            sinks.Add(RingBuffer);
            sinks.Add(new GodotConsoleSink(Configuration.Diagnostics?.IncludeCorrelation ?? false));

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
                "initialization",
                bootstrapLifetime.Token);

            if (Host.Services.TryGet<IYarnCommandAdapter>(out var adapter))
            {
                yarnCommandAdapter = adapter!;
                adapter!.CommandFailed += HandleCommandFailed;
            }

            if (Configuration.Diagnostics?.EnableOverlay == true &&
                Host.Services.TryGet<IRuntimeDiagnostics>(out var diagnostics) &&
                Host.Services.TryGet<IInputManager>(out var inputManager))
            {
                DebugOverlay = new DefaultDebugOverlay(
                    this, diagnostics!, inputManager!, Configuration.Diagnostics);
            }

            var showFps = Configuration.Diagnostics?.ShowFpsOverlay ?? true;
            if (showFps && OS.IsDebugBuild())
            {
                FpsOverlay = new DefaultFpsOverlay(this, Configuration.Diagnostics);
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
            .AddSingleton<ILocalizedResourceResolver>(
                resolver => new LocalizedResourceResolver(resolver.Get<IResourceManager>()),
                typeof(IResourceManager))
            .AddSingleton<IActorTextureLoader>(
                resolver => new ResourceActorTextureLoader(
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.FailOnMissingAssets,
                    resolver.Get<IMissingResourceReporter>(),
                    resolver.Get<ILocalizedResourceResolver>(),
                    resolver.Get<ILocaleManager>(),
                    [Configuration.Resources.CharactersPath, Configuration.Resources.BackgroundsPath]),
                typeof(IResourceManager),
                typeof(IMissingResourceReporter),
                typeof(ILocalizedResourceResolver),
                typeof(ILocaleManager))
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
                    resolver.Get<IMissingResourceReporter>(),
                    resolver.Get<ILocaleManager>(),
                    resolver.Get<ILocalizedResourceResolver>()),
                typeof(IResourceManager),
                typeof(IOperationScheduler),
                typeof(IMissingResourceReporter),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver))
            .AddSingleton<GodotActorHost>(_ => new GodotActorHost(this, Configuration!.Actors!))
            .AddSingleton<IActorManager>(
                resolver => CreateActorManager(
                    resolver.Get<GodotActorHost>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IActorTextureLoader>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<ILocaleManager>()),
                typeof(GodotActorHost),
                typeof(IOperationScheduler),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager))
            .AddSingleton<GodotDirectionRenderHost>(
                _ => new GodotDirectionRenderHost(
                    this,
                    Configuration!.Actors!.ReferenceResolution,
                    Configuration!.Stage!.MaxDirectionStages))
            .AddSingleton<GodotDirectionStageBackend>(
                resolver => new GodotDirectionStageBackend(
                    resolver.Get<GodotDirectionRenderHost>(),
                    new DirectionTextureLoader(resolver.Get<IResourceManager>()),
                    root => CreateDirectionActorManager(
                        root,
                        resolver.Get<IOperationScheduler>(),
                        resolver.Get<IActorTextureLoader>(),
                        resolver.Get<IFrameClock>(),
                        resolver.Get<ILocaleManager>())),
                typeof(GodotDirectionRenderHost),
                typeof(IResourceManager),
                typeof(IOperationScheduler),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager))
            .AddSingleton<IDirectionTransactionCoordinator>(
                resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var textures = new DirectionTextureLoader(
                        resolver.Get<IResourceManager>());
                    var backend = new CompositeDirectionPreparationBackend(
                        resolver.Get<GodotDirectionStageBackend>(),
                        new GodotDirectionViewBackend(host),
                        new GodotDirectionScreenBackend(host),
                        new GodotDirectionEffectBackend(host));
                    return new DirectionTransactionCoordinator(
                        new DirectionPlanner(),
                        backend);
                },
                typeof(GodotDirectionRenderHost),
                typeof(GodotDirectionStageBackend),
                typeof(IResourceManager))
            .AddSingleton<IDirectionBeatRuntime>(
                resolver => new DirectionBeatRuntime(
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    new TransientDirectionCueExecutor(
                        resolver.Get<ICameraManager>(),
                        resolver.Get<IScreenEffectManager>(),
                        resolver.Get<IStageCurtainManager>())),
                typeof(IDirectionTransactionCoordinator),
                typeof(ICameraManager),
                typeof(IScreenEffectManager),
                typeof(IStageCurtainManager))
            .AddSingleton<IDirectionScopeResolver>(_ => new DirectionScopeResolver())
            .AddSingleton<IScriptPreloadManager>(
                resolver => new ScriptPreloadManager(
                    resolver.Get<IResourceManager>(),
                    resolver.Get<IActorManager>(),
                    Logger),
                typeof(IResourceManager),
                typeof(IActorManager))
            .AddSingleton<IStageBackend>(
                resolver => new GodotStageBackend(this, resolver.Get<GodotActorHost>()),
                typeof(GodotActorHost))
            .AddSingleton<IStageAccessibility>(
                _ => new StageAccessibility(Configuration!.Stage!.ReduceMotion))
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler),
                typeof(IStageAccessibility))
            .AddSingleton<IStageTransitionManager>(
                resolver =>
                {
                    var registry = new StageTransitionProviderRegistry();
                    ConfigureStageTransitionProviders?.Invoke(registry);
                    return new StageTransitionManager(
                        resolver.Get<IStageBackend>(),
                        resolver.Get<IOperationScheduler>(),
                        registry.Providers);
                },
                typeof(IStageBackend),
                typeof(IOperationScheduler))
            .AddSingleton<IStageCurtainManager>(
                resolver => new StageCurtainManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler))
            .AddSingleton<IScreenEffectManager>(
                resolver => new ScreenEffectManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler),
                typeof(IStageAccessibility));

        var fontCatalog = new FontCatalog();
        if (Configuration?.Localization is not null)
        {
            fontCatalog.FontScale = Configuration.Localization.FontScale;
            fontCatalog.LineSpacing = Configuration.Localization.LineSpacing;
        }

        services
            .AddSingleton<IFontCatalog>(_ => fontCatalog)
            .AddSingleton<ILocaleManager>(
                resolver =>
                {
                    var defaultLoc = Configuration?.Localization?.DefaultLocale ?? Configuration?.Settings?.DialogueLocale ?? "en";
                    var defaultVoiceLoc = Configuration?.Settings?.VoiceLocale ?? Configuration?.Audio?.VoiceLocale ?? "en";
                    var supportedLocs = Configuration?.Localization?.SupportedLocales?.ToArray() ?? ["en", "zh-CN"];
                    LocaleManager? manager = null;
                    manager = new LocaleManager(
                        defaultLoc,
                        supportedLocs,
                        fontCatalog,
                        isBusyCheck: () => yarnCommandAdapter?.HasActiveCommands == true,
                        onLocaleApplied: locale =>
                        {
                            if (yarnDialogueBackend is not null)
                            {
                                yarnDialogueBackend.SetLocale(locale);
                            }

                            if (dialoguePlayer?.State == DialogueSessionState.Running &&
                                yarnDialogueBackend is not null &&
                                dialoguePresentation is not null)
                            {
                                var fallbackChain = manager!.GetDialogueFallbackChain(locale);
                                if (dialoguePlayer.IsPresentingOptions)
                                {
                                    var reResolvedOpts = yarnDialogueBackend.ReResolveCurrentOptions(locale, fallbackChain);
                                    dialoguePresentation.ReRenderOptions(reResolvedOpts);
                                }
                                else if (!string.IsNullOrWhiteSpace(dialoguePlayer.CurrentLineId))
                                {
                                    var reResolvedLine = yarnDialogueBackend.ReResolveCurrentLine(locale, fallbackChain);
                                    if (reResolvedLine is not null)
                                    {
                                        dialoguePresentation.ReRenderLine(reResolvedLine);
                                    }
                                }
                            }

                            return Task.CompletedTask;
                        },
                        applyEngineLocale: TranslationServer.SetLocale,
                        logger: Logger,
                        waitForSafeBoundary: async cancellationToken =>
                        {
                            if (yarnDialogueBackend is not null)
                            {
                                await yarnDialogueBackend.WaitForLocaleSwitchBoundaryAsync(cancellationToken);
                            }
                        },
                        defaultVoiceLocale: defaultVoiceLoc);
                    ConfigureLocales?.Invoke(manager);
                    return manager;
                });

        var presenterRegistry = new DialoguePresenterRegistry();
        ConfigureDialoguePresenters?.Invoke(presenterRegistry);
        var markupHandlerRegistry = new DialogueMarkupHandlerRegistry();
        ConfigureDialogueMarkupHandlers?.Invoke(markupHandlerRegistry);
        services
            .AddSingleton<IYarnProjectVersionStore>(
                resolver => new YarnProjectVersionStore(
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.ScriptsPath),
                typeof(IResourceManager))
            .AddSingleton<IYarnDialogueBackend>(
                resolver =>
                {
                    yarnDialogueBackend = new YarnDialogueBackend(
                        this,
                        resolver.Get<IResourceManager>(),
                        Configuration!.Resources!.ScriptsPath,
                        presenterRegistry.Presenters
                            .Concat<IDialoguePresenter>(
                                [new VoiceDialoguePresenter(
                                    resolver.Get<IAudioManager>(),
                                    resolver.Get<ILocaleManager>(),
                                    resolver.Get<ILocalizedResourceResolver>())])
                            .ToArray(),
                        resolver.Get<ILocaleManager>(),
                        ResolveSpeakerName,
                        Logger,
                        Configuration.Script!.RequireExactProgramFingerprint,
                        resolver.Get<IYarnProjectVersionStore>(),
                        resolver.Get<IDirectionScopeResolver>());
                    return yarnDialogueBackend;
                },
                typeof(IResourceManager),
                typeof(IAudioManager),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver),
                typeof(IYarnProjectVersionStore),
                typeof(IDirectionScopeResolver))
            .AddSingleton<IDialoguePresentation>(
                resolver =>
                {
                    dialoguePresentation = new YarnDefaultDialoguePresentation(
                        resolver.Get<IYarnDialogueBackend>(),
                        resolver.Get<IInputManager>(),
                        resolver.Get<IFrameClock>(),
                        Configuration!.Presentation!,
                        markupHandlerRegistry.Handlers,
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IFontCatalog>(),
                        ResolveSpeakerName);
                    return dialoguePresentation;
                },
                typeof(IYarnDialogueBackend),
                typeof(IInputManager),
                typeof(IFrameClock),
                typeof(ILocaleManager),
                typeof(IFontCatalog))
            .AddSingleton<IDialogueVariables>(
                resolver => new DialogueVariables(
                    resolver.Get<IYarnDialogueBackend>().VariableStore),
                typeof(IYarnDialogueBackend))
            .AddSingleton<DirectionBeatNodeBoundaryService>(
                resolver => new DirectionBeatNodeBoundaryService(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IDirectionBeatRuntime>()),
                typeof(IYarnDialogueBackend),
                typeof(IDirectionBeatRuntime))
            .AddSingleton<IYarnCommandAdapter>(
                resolver => CreateYarnCommandAdapter(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IActorManager>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IDirectionBeatRuntime>()),
                typeof(IYarnDialogueBackend),
                typeof(IActorManager),
                typeof(IOperationScheduler),
                typeof(IAudioManager),
                typeof(IDirectionBeatRuntime),
                typeof(DirectionBeatNodeBoundaryService))
            .AddSingleton<IDialoguePlayer>(
                resolver =>
                {
                    dialoguePlayer = new DialoguePlayer(
                        resolver.Get<IYarnDialogueBackend>(),
                        Configuration!.Dialogue!.DefaultProjectId,
                        Configuration.Dialogue.DefaultStartNode,
                        Logger);
                    return dialoguePlayer;
                },
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
            .AddSingleton<IYarnHotReloadService>(
                resolver =>
                {
                    var hotReload = Configuration!.Dialogue!.HotReload;
                    var configuredRoot = string.IsNullOrWhiteSpace(hotReload.SourceRootOverride)
                        ? System.IO.Path.Combine(
                            ProjectSettings.GlobalizePath(Configuration.Resources!.RootPath),
                            Configuration.Resources.ScriptsPath)
                        : hotReload.SourceRootOverride;
                    return new YarnHotReloadService(
                        resolver.Get<IYarnProjectVersionStore>(),
                        resolver.Get<IScriptPlayback>(),
                        resolver.Get<IYarnDialogueBackend>(),
                        hotReload,
                        Configuration.Dialogue.DefaultProjectId,
                        configuredRoot,
                        Logger,
                        playback: resolver.Get<IPlaybackModeManager>());
                },
                typeof(IYarnProjectVersionStore),
                typeof(IScriptPlayback),
                typeof(IYarnDialogueBackend),
                typeof(IPlaybackModeManager))
            .AddSingleton<IHistoryManager>(
                resolver => new HistoryManager(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IInputManager>(),
                    Configuration!.History!.Capacity,
                    Logger,
                    resolver.Get<ILocaleManager>(),
                    resolver.Get<ILocalizedResourceResolver>()),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IAudioManager),
                typeof(IInputManager),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver))
            .AddSingleton<IPlayedRegister>(
                resolver => new PlayedRegister(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    logger: Logger),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation))
            .AddSingleton<IPlaybackModeManager>(
                resolver => new PlaybackModeManager(
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IHistoryManager>(),
                    resolver.Get<IPlayedRegister>(),
                    resolver.Get<IOperationScheduler>(),
                    TimeSpan.FromSeconds(Configuration!.Auto!.ExtraDelaySeconds),
                    Configuration.Auto.WaitForVoice,
                    TimeSpan.FromSeconds(Configuration.Settings!.SkipDelaySeconds),
                    Configuration.Settings.SkipDefaultMode == SkipDefaultMode.ReadOnly
                        ? PlaybackMode.SkipReadOnly
                        : PlaybackMode.SkipAll,
                    Logger),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IAudioManager),
                typeof(IFrameClock),
                typeof(IInputManager),
                typeof(IHistoryManager),
                typeof(IPlayedRegister),
                typeof(IOperationScheduler));

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
            var customStateContributors = new StateContributorRegistry();
            ConfigureStateContributors?.Invoke(customStateContributors);
            var customContributors = customStateContributors.Build(
            [
                BuiltInStateKeys.DialogueVariables,
                BuiltInStateKeys.Actors,
                BuiltInStateKeys.AudioBgm,
                BuiltInStateKeys.ScriptPlayback,
                BuiltInStateKeys.SessionHistory,
                PlayedRegister.StateKey,
                "runtime.settings",
            ]);
            services.AddSingleton<IStateSerializer>(
                _ => new CanonicalJsonStateSerializer(stateMigrations));
            services.AddSingleton<IStateStorage>(
                _ => new GodotUserStateStorage(Configuration.State!.StorageNamespace));

            if (Configuration.Settings?.Enabled == true)
            {
                services.AddSingleton<ISettingsManager>(
                    resolver => new SettingsManager(
                        CreateSettingsDefaults(),
                        customContributors.Where(value =>
                            value.Scope == StateScope.Settings).ToArray(),
                        [new RuntimeSettingsProjection(
                            resolver.Get<IAudioManager>(),
                        resolver.Get<IPlaybackModeManager>(),
                        resolver.Get<IDialoguePresentation>(),
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IStageAccessibility>())],
                        resolver.Get<IStateSerializer>(),
                        resolver.Get<IStateStorage>(),
                        stateMigrations,
                        MajoRuntimeMetadata.PackageVersion,
                        Configuration.State.GameBuildId,
                        Configuration.State.StorageNamespace,
                        TimeSpan.FromSeconds(Configuration.Settings.FlushDebounceSeconds),
                        Logger,
                        resolver.Get<ILocaleManager>()),
                    typeof(IStateSerializer),
                    typeof(IStateStorage),
                    typeof(IAudioManager),
                    typeof(IPlaybackModeManager),
                    typeof(IDialoguePresentation),
                    typeof(ILocaleManager),
                    typeof(IStageAccessibility));
            }

            if (Configuration.GlobalState?.Enabled == true)
            {
                services.AddSingleton<IGlobalStateManager>(
                    resolver => new GlobalStateManager(
                        new IStateContributor[] { (IStateContributor)resolver.Get<IPlayedRegister>() }
                            .Concat(customContributors.Where(value =>
                                value.Scope == StateScope.Global)).ToArray(),
                        resolver.Get<IStateSerializer>(),
                        resolver.Get<IStateStorage>(),
                        stateMigrations,
                        MajoRuntimeMetadata.PackageVersion,
                        Configuration.State.GameBuildId,
                        Configuration.State.StorageNamespace,
                        TimeSpan.FromSeconds(Configuration.GlobalState.FlushDebounceSeconds),
                        Configuration.GlobalState.StrictRecovery,
                        Logger),
                    typeof(IStateSerializer),
                    typeof(IStateStorage),
                    typeof(IPlayedRegister));
            }

            if (Configuration.Settings?.Enabled == true &&
                Configuration.Settings.ShowDefaultUi)
            {
                services.AddSingleton<ISettingsOverlay>(
                    resolver => new DefaultSettingsOverlay(
                        this,
                        resolver.Get<ISettingsManager>(),
                        resolver.Get<IInputManager>(),
                        resolver.Get<ILocaleManager>(),
                        Configuration.Settings.DefaultUiScene),
                    typeof(ISettingsManager),
                    typeof(IInputManager),
                    typeof(ILocaleManager));
            }
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
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    stateMigrations,
                    customContributors.Where(value =>
                        value.Scope is StateScope.Game or StateScope.Session).ToArray()),
                typeof(IDialogueVariables),
                typeof(IActorManager),
                typeof(IAudioManager),
                typeof(IHistoryManager),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IInputManager),
                typeof(IOperationScheduler),
                typeof(IStateEligibility),
                typeof(IDirectionTransactionCoordinator));
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

            var travel = Configuration.State.DialogueTravel;
            if (travel.Enabled &&
                (OS.HasFeature("editor") ||
                 OS.IsDebugBuild() && travel.EnableInExportedDebugBuild))
            {
                services.AddSingleton<IDialogueTravelManager>(
                    resolver => new DialogueTravelManager(
                        resolver.Get<IStateManager>(),
                        resolver.Get<IGameSessionCoordinator>(),
                        resolver.Get<IScriptPlayback>(),
                        resolver.Get<IDialoguePlayer>(),
                        resolver.Get<IPlaybackModeManager>(),
                        travel.Capacity,
                        resolver.Get<IInputManager>(),
                        Logger),
                    typeof(IStateManager),
                    typeof(IGameSessionCoordinator),
                    typeof(IScriptPlayback),
                    typeof(IDialoguePlayer),
                    typeof(IPlaybackModeManager),
                    typeof(IInputManager));

                if (travel.ShowDefaultUi)
                {
                    services.AddSingleton<DefaultDialogueTravelOverlay>(
                        resolver => new DefaultDialogueTravelOverlay(
                            this,
                            resolver.Get<IDialogueTravelManager>(),
                            resolver.Get<IInputManager>(),
                            resolver.Get<IPlaybackModeManager>()),
                        typeof(IDialogueTravelManager),
                        typeof(IInputManager),
                        typeof(IPlaybackModeManager));
                }
            }

            if (Configuration.Save?.Enabled == true)
            {
                services.AddSingleton<ISaveThumbnailProvider>(
                    _ => Configuration.Save.CaptureThumbnails
                        ? new GodotSaveThumbnailProvider(
                            this,
                            Configuration.State.StorageNamespace,
                            Configuration.Save.ThumbnailWidth,
                            Configuration.Save.ThumbnailHeight)
                        : NullSaveThumbnailProvider.Instance);
                services.AddSingleton<ISaveSlotManager>(
                    resolver => new SaveSlotManager(
                        resolver.Get<IStateManager>(),
                        resolver.Get<IStateSerializer>(),
                        resolver.Get<IStateStorage>(),
                        resolver.Get<IStateEligibility>(),
                        resolver.Get<IGameSessionCoordinator>(),
                        resolver.Get<IHistoryManager>(),
                        new Dictionary<SaveSlotKind, int>
                        {
                            [SaveSlotKind.Manual] = Configuration.Save.ManualSlotCapacity,
                            [SaveSlotKind.Quick] = Configuration.Save.QuickSlotCapacity,
                            [SaveSlotKind.Auto] = Configuration.Save.AutoSlotCapacity,
                        },
                        MajoRuntimeMetadata.PackageVersion,
                        Configuration.State.GameBuildId,
                        Configuration.State.StorageNamespace,
                        resolver.Get<ISaveThumbnailProvider>(),
                        Logger),
                    typeof(IStateManager),
                    typeof(IStateSerializer),
                    typeof(IStateStorage),
                    typeof(IStateEligibility),
                    typeof(IGameSessionCoordinator),
                    typeof(IHistoryManager),
                    typeof(ISaveThumbnailProvider));
                services.AddSingleton<ISaveProductManager>(
                    resolver => new SaveProductManager(
                        resolver.Get<ISaveSlotManager>(),
                        resolver.Get<IDialoguePlayer>(),
                        resolver.Get<IDialoguePresentation>(),
                        resolver.Get<IScriptPlayback>(),
                        resolver.Get<IInputManager>(),
                        Configuration.Save.AutoSaveNodeIds,
                        Configuration.Save.AutoSaveLineIds,
                        Configuration.Save.AutoSaveMetadata,
                        Logger),
                    typeof(ISaveSlotManager),
                    typeof(IDialoguePlayer),
                    typeof(IDialoguePresentation),
                    typeof(IScriptPlayback),
                    typeof(IInputManager));

                if (Configuration.Save.ShowDefaultUi)
                {
                    services.AddSingleton<ISaveLoadOverlay>(
                        resolver => new DefaultSaveLoadOverlay(
                            this,
                            resolver.Get<ISaveSlotManager>(),
                            resolver.Get<IInputManager>(),
                            Configuration.Save.DefaultUiScene ??
                                GD.Load<PackedScene>(
                                    "res://addons/majo_runtime/Runtime/UI/SaveLoad/DefaultSaveLoadOverlay.tscn"),
                            resolver.Get<IFrameClock>(),
                            resolver.Get<IPlaybackModeManager>()),
                        typeof(ISaveSlotManager),
                        typeof(IInputManager),
                        typeof(IFrameClock),
                        typeof(IPlaybackModeManager));
                }
            }
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
        IDirectionTransactionCoordinator direction,
        StateMigrationRegistry migrations,
        IReadOnlyList<IStateContributor> customContributors)
    {
        var contributors = new StateContributorRegistry()
            .Register(new DialogueVariablesStateContributor(variables))
            .Register(new ActorStateContributor(actors))
            .Register(new AudioStateContributor(audio))
            .Register(new SessionHistoryStateContributor(history))
            .Register(new DirectionStateContributor(direction));
        foreach (var contributor in customContributors)
        {
            contributors.Register(contributor);
        }
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

    private RuntimeSettings CreateSettingsDefaults()
    {
        var audio = Configuration!.Audio!;
        var presentation = Configuration.Presentation!;
        var auto = Configuration.Auto!;
        var settings = Configuration.Settings!;
        return new RuntimeSettings(
            audio.MasterVolume,
            audio.BgmVolume,
            audio.SfxVolume,
            audio.VoiceVolume,
            audio.MasterMuted,
            audio.BgmMuted,
            audio.SfxMuted,
            audio.VoiceMuted,
            presentation.LettersPerSecond,
            auto.ExtraDelaySeconds,
            auto.WaitForVoice,
            settings.SkipDefaultMode,
            settings.SkipDelaySeconds,
            settings.ShowSpeakerName,
            settings.ShowTextBox,
            settings.DialogueLocale,
            settings.VoiceLocale,
            settings.FontScale,
            settings.LineSpacing,
            settings.ReduceMotion);
    }

    private YarnCommandAdapter CreateYarnCommandAdapter(
        IYarnDialogueBackend backend,
        IActorManager actors,
        IOperationScheduler operations,
        IAudioManager audio,
        IDirectionBeatRuntime direction)
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands);
        commands.RegisterModule(new AudioCommandModule(audio));
        commands.RegisterModule(new StageCommandModule());
        commands.RegisterModule(new DirectionCommandModule(
            direction,
            DirectionResourceResolver ??
                new DirectionResourceKeyResolver(Configuration!.Resources!),
            DirectionPositionResolver ??
                new Majo.Runtime.Dialogue.Commands.DirectionPositionResolver(
                    Configuration!.Actors!),
            Host!.Services.Get<IDirectionScopeResolver>(),
            () => dialoguePlayer?.CurrentNode));
        ConfigureCommands?.Invoke(commands);
        var catalog = commands.BuildCatalog();
        var converters = new CommandValueConverterRegistry();
        ConfigureCommandConverters?.Invoke(converters);
        var binder = new CommandBinder(
            new CommandBindingContext(
                Configuration!.Operations!.DefaultAnimationDurationSeconds,
                Configuration.Audio!.DefaultFadeDurationSeconds,
                Configuration.Operations.DefaultCharacterAnimationDurationSeconds,
                Configuration.Operations.DefaultCharacterAnimationKind),
            converters);
        var dispatcher = new RuntimeCommandDispatcher(
            binder,
            actors,
            operations,
            Host!.Services,
            Logger,
            Configuration.Dialogue!.AbortOnCommandError);
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
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales)
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
                new LayeredCharacterActorFactory(
                    host.CharacterLayer,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath),
                new BackgroundActorFactory(
                    host.BackgroundLayer,
                    scheduler,
                    textureLoader),
            },
            locales,
            Logger,
            new ActorCreationConventions(
                Configuration.Resources!.CharactersPath,
                Configuration.Resources.BackgroundsPath));
        ConfigureActorFactories?.Invoke(manager);
        return manager;
    }

    private ActorManager CreateDirectionActorManager(
        Node2D root,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales)
    {
        var actorConfiguration = Configuration!.Actors!;
        return new ActorManager(
            ActorConfigurationCompiler.Compile(
                actorConfiguration,
                Configuration.Resources!)
                .Where(definition =>
                    definition.FactoryId != ActorFactoryIds.Background)
                .ToArray(),
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    new CharacterPositionMap(actorConfiguration)),
                new LayeredCharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath),
            },
            locales,
            Logger,
            new ActorCreationConventions(
                Configuration.Resources!.CharactersPath,
                Configuration.Resources.BackgroundsPath));
    }

    private ResourceManager CreateResourceManager(
        IFrameClock frameClock,
        IRuntimeLifetime lifetime)
    {
        var resourceConfiguration = Configuration!.Resources!;
        var pathResolver = ResourcePathResolver ??
            new GodotProjectResourcePathResolver(resourceConfiguration.RootPath)
                .RegisterSourceExtensions<YarnProject>(YarnProject.YARN_PROJECT_EXTENSION)
                .RegisterSourceExtensions<AudioStream>("wav", "ogg", "mp3");
        var godotProvider = new GodotProjectResourceProvider(pathResolver, frameClock);
        var providers = new ResourceProviderRegistry().Register(godotProvider);
        ConfigureResourceProviders?.Invoke(providers);
        return new ResourceManager(
            providers.Providers,
            lifetime,
            Logger,
            memoryBudgetBytes: (long)resourceConfiguration.MemoryBudgetMiB * 1024 * 1024,
            defaultResourceEstimateBytes:
                (long)resourceConfiguration.DefaultResourceEstimateKiB * 1024);
    }

    private AudioManager CreateAudioManager(
        IResourceManager resources,
        IOperationScheduler operations,
        IMissingResourceReporter missingResources,
        ILocaleManager locales,
        ILocalizedResourceResolver localizedResources)
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
            missingResources,
            locales,
            localizedResources);
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
            yarnDialogueBackend = null;
            dialoguePlayer = null;
            dialoguePresentation = null;

            if (Host is not null)
            {
                // If initialization exceeded its deadline, RuntimeHost may still
                // own a service whose initializer deliberately ignored
                // cancellation. Let that initialization finish before asking the
                // host to shut down; applying the short shutdown deadline here
                // would cancel while the host is waiting on its lifecycle gate and
                // leave the late-completing service uncleaned.
                if (Host.State == RuntimeHostState.Initializing)
                {
                    await Host.ShutdownAsync(CancellationToken.None);
                }
                else
                {
                    await ExecuteWithLifecycleTimeoutAsync(
                        token => Host.ShutdownAsync(token),
                        "shutdown",
                        cancellationToken);
                }
            }

            FpsOverlay?.Dispose();
            FpsOverlay = null;
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
        string operationName,
        CancellationToken cancellationToken)
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

        cancellationToken.ThrowIfCancellationRequested();

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

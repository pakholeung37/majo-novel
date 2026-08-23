#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Input.Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
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

    public Action<ActorManager>? ConfigureActorFactories { get; set; }

    public Action<RuntimeCommandRegistry>? ConfigureCommands { get; set; }

    public Action<CommandValueConverterRegistry>? ConfigureCommandConverters { get; set; }

    public Action<ResourceProviderRegistry>? ConfigureResourceProviders { get; set; }

    public Action<DialoguePresenterRegistry>? ConfigureDialoguePresenters { get; set; }

    public Action<DialogueMarkupHandlerRegistry>? ConfigureDialogueMarkupHandlers { get; set; }

    public Func<Node, IAudioBackend>? AudioBackendFactory { get; set; }

    public RuntimeHost? Host { get; private set; }

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

            if (Configuration is null)
            {
                throw new MajoConfigurationException(new[]
                {
                    new ConfigurationIssue(nameof(Configuration), "Configuration is required."),
                });
            }

            var validation = Configuration.Validate();
            validation.ThrowIfInvalid();

            var services = new RuntimeServiceRegistry();
            RegisterDefaultServices(services);
            ConfigureServices?.Invoke(services);
            Host = services.Build(new GodotRuntimeLogger());
            await ExecuteWithLifecycleTimeoutAsync(
                token => Host.InitializeAsync(token),
                bootstrapLifetime.Token,
                "initialization");

            if (Host.Services.TryGet<IYarnCommandAdapter>(out var adapter))
            {
                yarnCommandAdapter = adapter!;
                adapter!.CommandFailed += HandleCommandFailed;
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
            .AddSingleton<IInputBackend>(
                _ => new GodotInputBackend(this, Configuration!.Input!))
            .AddSingleton<IInputManager>(
                resolver => new InputManager(
                    resolver.Get<IInputBackend>(),
                    Configuration!.Input!.Enabled),
                typeof(IInputBackend))
            .AddSingleton<IFrameClock>(_ => new GodotFrameClock(GetTree()))
            .AddSingleton<IResourceManager>(
                resolver => CreateResourceManager(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IOperationScheduler>(
                resolver => new OperationScheduler(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>(),
                    new GodotRuntimeLogger()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IAudioManager>(
                resolver => CreateAudioManager(
                    resolver.Get<IResourceManager>(),
                    resolver.Get<IOperationScheduler>()),
                typeof(IResourceManager),
                typeof(IOperationScheduler))
            .AddSingleton<GodotActorHost>(_ => new GodotActorHost(this))
            .AddSingleton<IActorManager>(
                resolver => CreateActorManager(
                    resolver.Get<GodotActorHost>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IResourceManager>()),
                typeof(GodotActorHost),
                typeof(IOperationScheduler),
                typeof(IResourceManager));

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
                    Configuration.Dialogue.DefaultStartNode),
                typeof(IYarnDialogueBackend),
                typeof(IYarnCommandAdapter),
                typeof(IDialoguePresentation));
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
            Host!.Services);
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
        IResourceManager resources)
    {
        var actorConfiguration = Configuration!.Actors!;
        var textureLoader = new ResourceActorTextureLoader(resources);
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
        return new ResourceManager(providers.Providers, lifetime);
    }

    private IAudioManager CreateAudioManager(
        IResourceManager resources,
        IOperationScheduler operations)
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
                configuration.VoiceLocale));
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

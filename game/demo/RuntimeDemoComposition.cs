#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;

namespace Majo.Demo;

public sealed class DemoSessionService : IRuntimeService
{
    private readonly IRuntimeLifetime lifetime;

    public DemoSessionService(IRuntimeLifetime lifetime)
    {
        this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    public string? SelectedRoute { get; private set; }

    public int InitializeCount { get; private set; }

    public int ResetCount { get; private set; }

    public int ShutdownCount { get; private set; }

    public bool RuntimeWasCancelledAtShutdown { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCount++;
        SelectedRoute = null;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownCount++;
        RuntimeWasCancelledAtShutdown = lifetime.RuntimeCancellationToken.IsCancellationRequested;
        return Task.CompletedTask;
    }

    public void SelectRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        SelectedRoute = route;
    }
}

public sealed class DemoPortraitActorFactory : IActorFactory
{
    public const string Id = "demo-portrait";

    private readonly Node2D host;
    private readonly Func<IOperationScheduler> scheduler;
    private readonly Func<IActorTextureLoader> textureLoader;
    private readonly CharacterPositionMap positions;

    public DemoPortraitActorFactory(
        Node2D host,
        Func<IOperationScheduler> scheduler,
        Func<IActorTextureLoader> textureLoader,
        CharacterPositionMap positions)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
    }

    string IActorFactory.FactoryId => Id;

    public int CreateCount { get; private set; }

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public IActor Create(ActorDefinition definition)
    {
        var view = new Sprite2D
        {
            Name = $"{definition.Id}-DemoPortrait",
            Centered = true,
        };
        host.AddChild(view);
        try
        {
            var actor = new CharacterActor(
                definition,
                view,
                scheduler(),
                textureLoader(),
                positions);
            CreateCount++;
            return actor;
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }
}

public static class RuntimeDemoComposition
{
    public const string DialogueProjectId = "main";

    public static MajoProjectConfiguration CreateConfiguration()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Presentation!.Enabled = true;
        configuration.Presentation.LettersPerSecond = 36;
        configuration.Auto!.ExtraDelaySeconds = 1;
        configuration.Operations!.DefaultAnimationDurationSeconds = 0.2;
        configuration.Diagnostics!.EnableOverlay = true;
        configuration.Diagnostics.EnableFileLogging = true;
        configuration.State!.StorageNamespace = "majo/runtime-demo";
        configuration.State.GameBuildId = "runtime-demo-phase-3";
        configuration.Save!.Enabled = true;
        configuration.Save.ManualSlotCapacity = 3;
        configuration.Save.QuickSlotCapacity = 2;
        configuration.Save.AutoSlotCapacity = 2;
        configuration.Save.ShowDefaultUi = true;
        configuration.Settings!.ShowDefaultUi = true;
        configuration.Settings.SkipDelaySeconds = 0.08;
        configuration.GlobalState!.Enabled = true;
        configuration.Actors!.LeftPosition = new Vector2(560, 610);
        configuration.Actors.CenterPosition = new Vector2(960, 610);
        configuration.Actors.RightPosition = new Vector2(1360, 610);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "backdrop",
            ActorFactoryIds.Background,
            "courtroom",
            CharacterPositionPreset.Center));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            DemoPortraitActorFactory.Id,
            "calm",
            CharacterPositionPreset.Right,
            configuration.Resources!.CharactersPath));
        return configuration;
    }

    public static DemoPortraitActorFactory ConfigureRuntime(
        MajoRuntimeBootstrap runtime,
        Node2D portraitHost,
        ActorConfiguration actorConfiguration,
        bool useFakeAudio = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(portraitHost);
        ArgumentNullException.ThrowIfNull(actorConfiguration);

        var factory = new DemoPortraitActorFactory(
            portraitHost,
            () => runtime.Host!.Services.Get<IOperationScheduler>(),
            () => runtime.Host!.Services.Get<IActorTextureLoader>(),
            new CharacterPositionMap(actorConfiguration));
        runtime.ConfigureServices += services =>
        {
            services.AddSingleton<DemoSessionService>(
                resolver => new DemoSessionService(resolver.Get<IRuntimeLifetime>()),
                typeof(IRuntimeLifetime));
            if (!useFakeAudio)
            {
                return;
            }

            var audio = new FakeAudioClipLoader("generated://runtime-demo")
                .Register("demo-theme")
                .Register("demo-tension")
                .Register("demo-chime")
                .Register("en/narrator-welcome")
                .Register("en/maya-ready");
            services.ReplaceSingleton<IAudioManager>(
                resolver => new AudioManager(
                    new FakeAudioBackend(),
                    resolver.Get<IOperationScheduler>(),
                    audio,
                    audio,
                    audio,
                    new AudioSettings(),
                    runtime.Logger,
                    resolver.Get<IMissingResourceReporter>()),
                typeof(IOperationScheduler),
                typeof(IMissingResourceReporter));
        };
        runtime.ConfigureActorFactories += actors => actors.RegisterFactory(factory);
        runtime.ConfigureCommands += RegisterCommands;
        return factory;
    }

    public static void RegisterCommands(RuntimeCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Register<DemoRouteCommandArguments>(SelectRouteAsync);
    }

    private static Task SelectRouteAsync(
        DemoRouteCommandArguments arguments,
        RuntimeCommandContext context)
    {
        if (arguments.Route is not "press" and not "evidence")
        {
            throw new RuntimeCommandException(
                "demo_route", $"Unknown route '{arguments.Route}'.");
        }

        var services = context.Require(context.Services, "ServiceResolver");
        services.Get<DemoSessionService>().SelectRoute(arguments.Route);
        return Task.CompletedTask;
    }

    private static ActorDefinitionConfiguration CreateActorDefinition(
        string id,
        string factoryId,
        string defaultAppearance,
        CharacterPositionPreset initialPosition,
        string resourcePathPrefix = "")
    {
        var definition = new ActorDefinitionConfiguration
        {
            Id = id,
            FactoryId = factoryId,
            DefaultAppearance = defaultAppearance,
            InitialPosition = initialPosition,
            ResourcePathPrefix = resourcePathPrefix,
        };
        return definition;
    }
}

[RuntimeCommand(
    "demo_route",
    Category = "Demo",
    Usage = "demo_route <press|evidence>")]
public sealed class DemoRouteCommandArguments
{
    [CommandParameter("route", 0, Primary = true, Required = true)]
    public string Route { get; set; } = string.Empty;
}

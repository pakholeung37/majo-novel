#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;

namespace Majo.Demo;

public sealed class M5CaseService : IRuntimeService
{
    private readonly IRuntimeLifetime lifetime;

    public M5CaseService(IRuntimeLifetime lifetime)
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

public sealed class M5PortraitActorFactory : IActorFactory
{
    public const string Id = "m5-portrait";

    private readonly Node2D host;
    private readonly Func<IOperationScheduler> scheduler;
    private readonly IActorTextureLoader textureLoader;
    private readonly CharacterPositionMap positions;

    public M5PortraitActorFactory(
        Node2D host,
        Func<IOperationScheduler> scheduler,
        IActorTextureLoader textureLoader,
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
            Name = $"{definition.Id}-M5Portrait",
            Centered = true,
        };
        host.AddChild(view);
        try
        {
            var actor = new CharacterActor(
                definition,
                view,
                scheduler(),
                textureLoader,
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

public static class M5DemoComposition
{
    public const string YarnProjectPath = "res://demo/M5Demo.yarnproject";

    public static MajoProjectConfiguration CreateConfiguration()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Operations!.DefaultAnimationDurationSeconds = 0.2;
        configuration.Actors!.LeftPosition = new Vector2(560, 610);
        configuration.Actors.CenterPosition = new Vector2(960, 610);
        configuration.Actors.RightPosition = new Vector2(1360, 610);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "backdrop",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("courtroom", "res://demo/assets/courtroom.svg"),
            ("spotlight", "res://demo/assets/courtroom_spotlight.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            M5PortraitActorFactory.Id,
            CharacterPositionPreset.Right,
            ("calm", "res://demo/assets/hero_calm.svg"),
            ("determined", "res://demo/assets/hero_determined.svg")));
        return configuration;
    }

    public static M5PortraitActorFactory ConfigureRuntime(
        MajoRuntimeBootstrap runtime,
        Node2D portraitHost,
        ActorConfiguration actorConfiguration)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(portraitHost);
        ArgumentNullException.ThrowIfNull(actorConfiguration);

        var factory = new M5PortraitActorFactory(
            portraitHost,
            () => runtime.Host!.Services.Get<IOperationScheduler>(),
            new ResourceActorTextureLoader(),
            new CharacterPositionMap(actorConfiguration));

        runtime.ConfigureServices += services => services.AddSingleton<M5CaseService>(
            resolver => new M5CaseService(resolver.Get<IRuntimeLifetime>()),
            typeof(IRuntimeLifetime));
        runtime.ConfigureActorFactories += actors => actors.RegisterFactory(factory);
        runtime.ConfigureCommands += RegisterCommands;
        return factory;
    }

    public static void RegisterCommands(RuntimeCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Register(
            "demo_route",
            (arguments, context) => SelectRouteAsync(arguments, context));
    }

    private static Task SelectRouteAsync(
        IReadOnlyList<string> arguments,
        RuntimeCommandContext context)
    {
        if (arguments.Count != 1)
        {
            throw new RuntimeCommandException(
                "demo_route", "Usage: demo_route <press|evidence>.");
        }

        if (arguments[0] is not "press" and not "evidence")
        {
            throw new RuntimeCommandException(
                "demo_route", $"Unknown route '{arguments[0]}'.");
        }

        var services = context.Require(context.Services, "ServiceResolver");
        services.Get<M5CaseService>().SelectRoute(arguments[0]);
        return Task.CompletedTask;
    }

    private static ActorDefinitionConfiguration CreateActorDefinition(
        string id,
        string factoryId,
        CharacterPositionPreset initialPosition,
        params (string Id, string Path)[] appearances)
    {
        var definition = new ActorDefinitionConfiguration
        {
            Id = id,
            FactoryId = factoryId,
            DefaultAppearance = appearances[0].Id,
            InitialPosition = initialPosition,
        };
        foreach (var appearance in appearances)
        {
            definition.Appearances.Add(new ActorAppearanceConfiguration
            {
                Id = appearance.Id,
                TexturePath = appearance.Path,
            });
        }

        return definition;
    }
}

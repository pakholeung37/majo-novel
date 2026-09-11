#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;

namespace CoreConsumerSmoke;

public partial class Smoke : Node
{
    public override async void _Ready()
    {
        try
        {
            await RunAsync();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        var textureRoot = new Node2D();
        AddChild(textureRoot);
        using var loader = new ResourceActorTextureLoader(failOnMissingAssets: true);
        using var settings = new ActorConfiguration();
        var definition = new ActorDefinition("hero", ActorFactoryIds.Character,
            new TextureActorDefinitionData("hero", "characters", "default",
                new Dictionary<string, string> { ["default"] = "res://actor.svg" }));
        using var manager = new ActorManager([definition],
            [new CharacterActorFactory(textureRoot, loader, new CharacterPositionMap(settings))]);
        await manager.InitializeAsync(CancellationToken.None);
        var actor = await manager.CreateAsync("hero");
        if (!actor.Visible || ((TextureCharacterActor)actor).View.Texture is null)
            throw new InvalidOperationException("Default texture actor did not become visible.");
        await manager.ShutdownAsync(CancellationToken.None);

        var customRoot = new Node2D();
        AddChild(customRoot);
        var registry = new ActorFactoryRegistry();
        registry.Register("consumer-custom", ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            context => new ConsumerFactory(context.CharacterRoot, context.CharacterPositions));
        registry.Seal();
        if (!registry.Contains("consumer-custom"))
            throw new InvalidOperationException("Public custom factory registration failed.");

        using var customManager = new ActorManager(
            [new ActorDefinition("custom", "consumer-custom")],
            [new ConsumerFactory(customRoot, new CharacterPositionMap(settings))]);
        await customManager.InitializeAsync(CancellationToken.None);
        var custom = await customManager.CreateAsync("custom");
        if (custom is not ConsumerActor || !custom.Visible)
            throw new InvalidOperationException("Public character base could not create a custom actor.");
        await customManager.ShutdownAsync(CancellationToken.None);
    }

    private sealed class ConsumerFactory(Node2D root, CharacterPositionMap positions) : IActorFactory
    {
        public string FactoryId => "consumer-custom";

        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = new Node2D { Name = definition.Id, Visible = false };
            root.AddChild(view);
            var actor = new ConsumerActor(definition, view, positions);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
    }

    private sealed class ConsumerActor(ActorDefinition definition, Node2D view,
        CharacterPositionMap positions)
        : GodotCharacterActorBase(definition, view, positions,
            CharacterPositionPreset.Center, "default")
    {
        public override void SetAppearance(string appearance) =>
            PersistentWrite(() => Appearance = appearance);

        public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
            string sourceAppearance, string appearance, AnimationSpec animation,
            ITransitionDriver driver, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This smoke actor intentionally supports cut presentation only.");
    }
}

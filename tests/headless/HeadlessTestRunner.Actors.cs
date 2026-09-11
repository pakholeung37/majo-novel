using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateActorSystemAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests/headless/fixtures";
        configuration.Actors!.ReferenceResolution = new Vector2I(1280, 720);
        configuration.Actors.LeftPosition = new Vector2(100, 200);
        configuration.Actors.CenterPosition = new Vector2(200, 200);
        configuration.Actors.RightPosition = new Vector2(300, 200);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            CharacterPositionPreset.Left,
            ("red", "res://tests/headless/fixtures/character_red.svg"),
            ("blue", "res://tests/headless/fixtures/character_blue.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "courtroom",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg"),
            ("wide", "res://tests/headless/fixtures/background_wide.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "gallery",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg")));

        var compilations = 0;
        var factoryContexts = new List<ActorFactoryContext>();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "ActorRuntime",
            Configuration = configuration,
            ConfigureActorFactories = registry =>
            {
                var registration = registry.GetRequired(ActorFactoryIds.Character);
                registry.Replace(registration.FactoryId, registration.Domains,
                    context => { factoryContexts.Add(context); return registration.BuildFactory(context); },
                    (definition, resources) =>
                    {
                        compilations++;
                        return registration.CompileDefinition!(definition, resources);
                    }, registration.ServiceDependencies.ToArray());
            },
        };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var stageViewport = bootstrap.GetNode<SubViewport>("MajoStageViewport");
        var stageBackdrop = stageViewport.GetNode<ColorRect>("MajoStageBackdrop/Black");
        var actorRoot = stageViewport.GetNode<CanvasLayer>("MajoActors");
        var cameraOutput = bootstrap.GetNode<CanvasLayer>("MajoCameraOutput");
        var backgroundLayer = actorRoot.GetNode<Control>("Backgrounds");
        var characterLayer = actorRoot.GetNode<Node2D>("Characters");
        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationRunner>();
        var activities = bootstrap.Host.Services.Get<IRuntimeActivityObserver>();
        var character = (TextureCharacterActor)manager.GetActor<ICharacterActor>(
            (await manager.CreateAsync("hero")).Id);
        var background = manager.GetActor<IBackgroundActor>(
            (await manager.CreateAsync("courtroom")).Id);

        Assert(character.SceneNode is Sprite2D,
            "Character factory did not create a Sprite2D.");
        Assert(ReferenceEquals(character.SceneNode.GetParent(), characterLayer),
            "Character Sprite2D was created under the wrong layer.");
        Assert(background.SceneNode is Control,
            "Background factory did not create a Control host.");
        Assert(ReferenceEquals(background.SceneNode.GetParent(), backgroundLayer),
            "Background host was created under the wrong layer.");
        Assert(ReferenceEquals(background.CurrentView.GetParent(), background.SceneNode),
            "Background TextureRect was created under the wrong host.");
        Assert(character.View.Texture is not null, "Character texture was not loaded.");
        Assert(background.CurrentView.Texture is not null, "Background texture was not loaded.");
        background.SetAppearance("wide");
        Assert(background.CurrentView.Size.X > stageViewport.Size.X &&
                Mathf.IsEqualApprox(background.CurrentView.Size.Y, stageViewport.Size.Y),
            "Wide background bleed was cropped before Camera shake could reveal it.");
        background.SetAppearance("dark");
        Assert(cameraOutput.GetNode<TextureRect>("LiveOutput").Texture is ViewportTexture,
            "Camera output did not present the isolated Stage viewport texture.");
        Assert(stageBackdrop.Color == Colors.Black &&
                stageBackdrop.GetParent<CanvasLayer>().Layer < actorRoot.Layer,
            "Camera Stage did not retain an explicit black background below its actors.");
        Assert(character.Position.IsEqualApprox(configuration.Actors.LeftPosition),
            "Character did not use the configured left position preset.");
        character.SetZIndex(15);
        Assert(character.ZIndex == 15 && character.View.ZIndex == 15,
            "Actor z-index did not propagate to its Godot CanvasItem.");
        _ = AssertThrows<ArgumentOutOfRangeException>(() =>
            character.SetZIndex(ActorZIndexRange.Maximum + 1));

        var initialTexture = character.View.Texture;
        character.SetAppearance("blue");
        Assert(character.Appearance == "blue", "Immediate appearance state was not updated.");
        Assert(!ReferenceEquals(initialTexture, character.View.Texture),
            "Character appearance did not replace its Texture2D.");
        character.SetAppearance("missing");
        Assert(character.Appearance == "missing",
            "Missing appearance did not preserve the requested logical state.");
        Assert(character.View.Texture is PlaceholderTexture2D,
            "Missing appearance did not use the development placeholder texture.");
        var missingResources = bootstrap.Host.Services.Get<IMissingResourceReporter>();
        var missingOccurrence = missingResources.Occurrences.Single();
        Assert(missingOccurrence.ResourceId == "characters/hero/missing" &&
            missingOccurrence.Fallback == "placeholder",
            "Missing appearance was not reported with its placeholder fallback.");
        var snapshot = bootstrap.Diagnostics!.CaptureSnapshot();
        Assert(snapshot.MissingResources?.UniqueCount == 1,
            "Diagnostics did not expose the missing appearance.");

        var strictLoader = new ResourceActorTextureLoader(
            bootstrap.Host.Services.Get<IResourceManager>(),
            failOnMissingAssets: true);
        _ = AssertThrows<ResourceNotFoundException>(() => strictLoader.Load(
            new ResourceKey("characters/hero/strict-missing")));

        var beats = bootstrap.Host.Services.Get<IDirectionBeatRuntime>();
        var renderHost = bootstrap.Host.Services.Get<GodotDirectionRenderHost>();
        var missingBackdrop = new ResourceKey("backgrounds/headless-missing");
        beats.Begin("missing-backdrop", "test");
        await beats.SubmitAsync(new BackdropPatch(StageId.Main,
            new BackdropState(missingBackdrop), DirectionTransitionSpec.Cut));
        await beats.EndAsync();
        await beats.JoinAsync("missing-backdrop");
        Assert(!beats.HasOpenBeat && renderHost.Backdrop.Texture is PlaceholderTexture2D,
            "Missing backdrop must settle its beat with a placeholder.");
        Assert(renderHost.PublishedBackdrop?.Resource == missingBackdrop &&
            missingResources.Occurrences.Any(entry => entry.ResourceId == missingBackdrop.Path),
            "Missing backdrop must preserve its logical resource and report the fallback.");
        beats.Begin("after-missing-backdrop", "test");
        await beats.SubmitAsync(new BackdropPatch(StageId.Main,
            new BackdropState(new ResourceKey("background_dark.svg")),
            DirectionTransitionSpec.Cut));
        await beats.EndAsync();
        Assert(renderHost.Backdrop.Texture is not null and not PlaceholderTexture2D,
            "A subsequent beat must continue and load a valid backdrop.");
        var strictDirectionLoader = new DirectionTextureLoader(strictLoader);
        try
        {
            await strictDirectionLoader.LoadAsync(missingBackdrop);
            throw new InvalidOperationException("Strict direction loading accepted a missing backdrop.");
        }
        catch (ResourceNotFoundException) { }

        // Later instance environments must reuse the original catalog, even if project
        // configuration is edited after the runtime has started.
        var capturedLeft = configuration.Actors.LeftPosition;
        var capturedCharactersPath = configuration.Resources.CharactersPath;
        configuration.Actors.LeftPosition = new Vector2(9000, 9000);
        configuration.Resources.CharactersPath = "changed-after-start";
        configuration.Actors.Actors.Clear();
        var direction = bootstrap.Host.Services.Get<IDirectionTransactionCoordinator>();
        await direction.ExecuteAsync(new[]
        {
            ActorPatch.Place(StageId.Main, new ActorId("hero-left")) with { DefinitionId = "hero" },
            ActorPatch.Place(StageId.Main, new ActorId("hero-right")) with { DefinitionId = "hero" },
        }, DirectionCompletionBarrier.Settle);
        Assert(compilations == 1 && factoryContexts.Count == 3,
            "Creating Direction bindings recompiled definitions or skipped the registered factory.");
        Assert(factoryContexts.Select(context => context.CharacterRoot).Distinct().Count() == 3,
            "Actor environments reused a scene root.");
        Assert(factoryContexts.All(context =>
            ReferenceEquals(context.ActorSettings, factoryContexts[0].ActorSettings) &&
            ReferenceEquals(context.ResourceSettings, factoryContexts[0].ResourceSettings) &&
            context.CharacterPositions.Get(CharacterPositionPreset.Left) == capturedLeft &&
            context.ResourceSettings.CharactersPath == capturedCharactersPath),
            "Factory settings or position maps escaped the runtime's immutable snapshot.");
        configuration.Actors.LeftPosition = capturedLeft;
        configuration.Resources.CharactersPath = capturedCharactersPath;

        character.SetPosition(CharacterPositionPreset.Right);
        character.SetOpacity(1);
        await bootstrap.ResetRuntimeAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(manager.Actors.Count == 0, "Runtime Reset did not clear ActorManager state.");
        Assert(!activities.HasActiveActivities(RuntimeActivityOwner.Other),
            "Runtime Reset left an active Actor operation.");
        Assert(characterLayer.GetChildCount() == 0,
            "Runtime Reset left Character nodes in the scene.");
        Assert(backgroundLayer.GetChildCount() == 0,
            "Runtime Reset left Background nodes in the scene.");

        var closingCharacter = await manager.CreateAsync("hero");
        var closingBackground = await manager.CreateAsync("courtroom");
        var closingCharacterNode = ((IGodotActorView)closingCharacter).SceneNode;
        var closingBackgroundNode = ((IGodotActorView)closingBackground).SceneNode;
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(closingCharacterNode),
            "Runtime shutdown left a Character node behind.");
        Assert(!IsInstanceValid(closingBackgroundNode),
            "Runtime shutdown left a Background node behind.");
        Assert(!IsInstanceValid(actorRoot), "Runtime shutdown left the Actor root behind.");
        Assert(!IsInstanceValid(stageViewport), "Runtime shutdown left the Stage viewport behind.");
        Assert(!IsInstanceValid(cameraOutput), "Runtime shutdown left the Camera output behind.");
        Assert(!IsInstanceValid(bootstrap), "Actor Runtime node leaked after shutdown.");
    }
}

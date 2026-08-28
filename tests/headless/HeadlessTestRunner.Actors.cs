using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Stage;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateActorSystemAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Actors!.LeftPosition = new Vector2(100, 200);
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

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "ActorRuntime",
            Configuration = configuration,
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
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var character = manager.Get<ICharacterActor>(
            manager.Create("hero").Id);
        var background = manager.Get<IBackgroundActor>(
            manager.Create("courtroom").Id);

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

        character.SetPosition(configuration.Actors.LeftPosition);
        var firstMove = character.ChangePosition(
            CharacterPositionPreset.Center, 1, "first-move");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var secondMove = character.ChangePosition(
            CharacterPositionPreset.Right, 0.04, "second-move");
        await firstMove.Completion;
        Assert(firstMove.State == OperationState.Completed,
            "Conflicting prior position operation was not force-completed.");
        Assert(character.Position.IsEqualApprox(configuration.Actors.CenterPosition),
            "Prior position operation did not reach its target before the next began.");
        await secondMove.Completion;
        Assert(character.Position.IsEqualApprox(configuration.Actors.RightPosition),
            "Second position operation did not reach its target.");

        character.SetOpacity(0);
        var fade = character.ChangeOpacity(1, 0.08, "character-fade");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(character.Opacity is > 0 and < 1,
            "Character opacity Tween did not expose an intermediate value.");
        Assert(Mathf.IsEqualApprox(character.TargetOpacity, 1),
            "Character target opacity did not preserve the requested final state.");
        await fade.Completion;
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Character opacity Tween did not reach its final value.");

        var interruptedHide = character.Hide(1, "interrupted-hide");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var replacementShow = character.Show(0.04, "replacement-show");
        await interruptedHide.Completion;
        await replacementShow.Completion;
        Assert(character.Visible,
            "Show interrupting Hide left the Character invisible.");
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Show interrupting Hide did not reach full opacity.");

        var oldBackgroundView = background.CurrentView;
        var crossFade = background.CrossFadeAppearance("light", 0.06, "background-crossfade");
        Assert(background.TargetAppearance == "light" && background.Appearance == "dark",
            "Background did not distinguish target and rendered appearance state.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(background.SceneNode.GetChildCount() == 2,
            "Background crossfade did not retain old and new TextureRects concurrently.");
        await crossFade.Completion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(background.Appearance == "light",
            "Background appearance state did not reach the crossfade target.");
        Assert(!IsInstanceValid(oldBackgroundView),
            "Background crossfade did not release the old TextureRect.");
        Assert(background.SceneNode.GetChildCount() == 1,
            "Background crossfade left an extra TextureRect behind.");

        var actorCommands = new ActorCommandService(manager);
        await actorCommands.BackAsync(new BackCommand(
            "gallery",
            "dark",
            0.06,
            true,
            null,
            "background-main-slot"));
        var gallery = manager.Get<IBackgroundActor>("gallery");
        Assert(gallery.Slot == BackgroundSlotIds.Main && gallery.Visible,
            "Back command did not activate its target in the default main slot.");
        Assert(!background.Visible,
            "Back command left the previous main-slot Background visible.");
        Assert(backgroundLayer.GetChildren().Count(child =>
                child is CanvasItem canvas && canvas.Visible) == 1,
            "Main Background slot retained more than one stable visible Actor.");

        var camera = bootstrap.Host.Services.Get<ICameraManager>();
        var focusPose = new CameraState(new Vector2(37.384f, 74.816f), 3, 10);
        camera.ApplyStableState(focusPose);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var cameraDissolve = camera.ResetCamera(new CameraViewTransitionSpec(
            CameraViewTransitionKind.Dissolve,
            new StageOperationSpec(0.2)));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var transitionSource = cameraOutput.GetNode<TextureRect>("TransitionSource");
        Assert(transitionSource.Visible && transitionSource.Texture is ImageTexture,
            "Camera dissolve did not retain a captured source Camera output.");
        Assert(transitionSource.Modulate.A is > 0 and < 1,
            "Camera dissolve did not expose an intermediate mix value.");
        Assert(camera.TargetState == CameraState.Default,
            "Camera dissolve did not publish its stable target pose.");
        await cameraDissolve.Completion;
        Assert(camera.State == CameraState.Default &&
                !transitionSource.Visible &&
                transitionSource.Texture is null,
            "Camera dissolve did not commit its target and release the source capture.");

        var resetOperation = character.ChangePosition(
            CharacterPositionPreset.Left, 10, "reset-character");
        await bootstrap.ResetRuntimeAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(resetOperation.State == OperationState.Cancelled,
            "Runtime Reset did not cancel the Actor operation.");
        Assert(manager.Actors.Count == 0, "Runtime Reset did not clear ActorManager state.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Runtime Reset left an active Actor operation.");
        Assert(characterLayer.GetChildCount() == 0,
            "Runtime Reset left Character nodes in the scene.");
        Assert(backgroundLayer.GetChildCount() == 0,
            "Runtime Reset left Background nodes in the scene.");

        var closingCharacter = manager.Create("hero");
        var closingBackground = manager.Create("courtroom");
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

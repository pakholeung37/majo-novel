using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;

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
			("light", "res://tests/headless/fixtures/background_light.svg")));

		var bootstrap = new MajoRuntimeBootstrap
		{
			Name = "ActorRuntime",
			Configuration = configuration,
		};
		AddChild(bootstrap);
		await bootstrap.InitializationCompletion;

		var actorRoot = bootstrap.GetNode<CanvasLayer>("MajoActors");
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
		Assert(character.Position.IsEqualApprox(configuration.Actors.LeftPosition),
			"Character did not use the configured left position preset.");

		var initialTexture = character.View.Texture;
		character.SetAppearance("blue");
		Assert(character.Appearance == "blue", "Immediate appearance state was not updated.");
		Assert(!ReferenceEquals(initialTexture, character.View.Texture),
			"Character appearance did not replace its Texture2D.");
		var missingAppearance = AssertThrows<ResourceNotFoundException>(() =>
			character.SetAppearance("missing"));
		Assert(missingAppearance.Candidates.Any(path => path.StartsWith(
				"res://assets/characters/hero/missing.",
				StringComparison.Ordinal)),
			"Missing conventional appearance did not report resolved candidates.");

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
		Assert(!IsInstanceValid(bootstrap), "Actor Runtime node leaked after shutdown.");
	}
}

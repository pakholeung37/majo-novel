#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateLayeredCharacterAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests/headless/fixtures/resources";
        configuration.Actors!.ReferenceResolution = new Vector2I(2560, 1440);
        configuration.Actors.ScenePositionOffset = new Vector2(0, 10);
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "LayeredCharacterRuntime",
            Configuration = configuration,
        };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var actors = bootstrap.Host!.Services.Get<IActorManager>();
        Assert(bootstrap.GetNode<SubViewport>("MajoStageViewport").Size ==
                new Vector2I(2560, 1440),
            "Layered character Stage did not retain the configured reference resolution.");
        var preloader = bootstrap.Host.Services.Get<IScriptPreloadManager>();
        await preloader.PrepareAsync(new ScriptPreloadWindow(
            "headless-layered-character",
            new ResourcePreloadManifest(
                "headless-layered-character-resources",
                [ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("characters/LayeredA/atlas"),
                    estimatedBytes: 1024L * 1024 * 4)]),
            [new ActorWarmupRequest(
                "LayeredA",
                ActorFactoryIds.Character,
                ["Default", "Surprised1"],
                CharacterPositionPreset.Center)]));
        var character = (ICharacterActor)await actors.GetOrCreateAsync(new ActorSpawnRequest(
            "LayeredA",
            ActorFactoryIds.Character,
            "Default",
            CharacterPositionPreset.Center));
        Assert(!character.Visible &&
                preloader.CurrentWindowId == "headless-layered-character",
            "Layered character preload did not create a dormant ready actor.");
        Assert(actors.GetPreloadResources(new ActorSpawnRequest("LayeredA", ActorFactoryIds.Character, "Surprised1")).Count == 0,
            "An existing layered actor redundantly requested its resident atlases for preload.");
        character.SetVisible(true);
        var layerComposite = character.View.GetNode<CanvasGroup>("LayerComposite");
        var layers = layerComposite.GetChildren().OfType<Sprite2D>().ToArray();
        Assert(character.FactoryId == ActorFactoryIds.LayeredCharacter,
            "Layered character used the wrong actor factory.");
        Assert(layers.Length == 9,
            "Layered character did not create every synthetic fixture layer.");
        Assert(layers.All(layer => layer.ZIndex == 0),
            "Layered character layers escaped the CanvasGroup Z batch.");
        var facialLine = layers.Single(layer =>
            layer.Name == "Face_Outline");
        var normalCheeks = layers.Single(layer =>
            layer.Name == "Face_Cheeks");
        Assert(facialLine.Material is ShaderMaterial multiplyMaterial &&
                multiplyMaterial.Shader?.Code.Contains(
                    "mix(vec3(1.0)", System.StringComparison.Ordinal) == true &&
                normalCheeks.Material is null,
            "Layered character did not isolate transparent Multiply pixels from default layers.");

        character.SetScenePosition(new Vector2(45, 0));
        character.SetScale(new Vector2(0.75f, 0.75f));
        Assert(character.Position.IsEqualApprox(new Vector2(1152, 1296)) &&
                character.Scale.IsEqualApprox(new Vector2(0.75f, 0.75f)),
            "Layered character did not map scene percentages, baseline offset, or root scale correctly.");

        var previousAppearance = character.Appearance;
        // Runtime actors are culled separately from backgrounds. Transient overlays
        // must retain the actor's non-default layer in both appearance APIs.
        Majo.Runtime.Direction.Godot.GodotDirectionRenderHost.AssignVisibilityLayer(
            character.View, Majo.Runtime.Direction.Godot.GodotDirectionRenderHost.SubjectVisibilityLayer);
        var actorHost = character.View.GetParent();
        var nodesBeforeTransition = actorHost.GetChildren().ToHashSet();
        var actorChildrenBeforeTransition = character.View.GetChildren().ToHashSet();
        var appearanceTransition = character.ChangeAppearance("Surprised1", 0.2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(character.TargetAppearance != previousAppearance &&
                Mathf.IsEqualApprox(layerComposite.SelfModulate.A, 1) &&
                layers.All(l => Mathf.IsEqualApprox(l.SelfModulate.A, 1)) &&
                character.View.GetNodeOrNull<Sprite2D>($"{character.View.Name}_DissolveOverlay") is not null,
            "Layered character appearance change did not initialize endpoint dissolve.");
        Assert(actorHost.GetChildren().All(nodesBeforeTransition.Contains),
            "Layered character appearance change changed its actor host nodes.");
        Assert(character.View.GetNode<Sprite2D>($"{character.View.Name}_DissolveOverlay").VisibilityLayer ==
            character.View.VisibilityLayer, "Direct appearance overlay must be visible to the subject camera.");
        await appearanceTransition.Completion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Layered character appearance change did not finish its dissolve phase.");
        Assert(character.View.GetChildren().ToHashSet().SetEquals(actorChildrenBeforeTransition),
            "Layered character appearance change left transient scene nodes.");
        using (var prepared = await ((IActorAppearanceTarget)character).PrepareAppearanceTransitionAsync(
            "Default", new AnimationSpec(.2),
            new Majo.Runtime.Direction.Transitions.DissolveTransitionDriver()))
        {
            prepared.Commit();
            var preparedOverlay = character.View.GetChildren().OfType<Sprite2D>().Single();
            Assert(preparedOverlay.VisibilityLayer == character.View.VisibilityLayer && preparedOverlay.Visible,
                "Prepared appearance overlay must retain the subject layer after reparenting.");
            prepared.ApplyProgress(.5f);
            Assert(!layerComposite.Visible && preparedOverlay.Visible,
                "Appearance transition must keep a visible replacement while the stable actor is hidden.");
            prepared.Finish();
            Assert(layerComposite.Visible, "Finishing appearance must restore the stable actor.");
        }
        character.SetAppearance("Surprised1");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(actorHost.GetChildren().All(nodesBeforeTransition.Contains),
            "Layered character appearance change changed its actor host nodes.");
        Assert(LayerVisible(layers,
                "Eyes_Surprised") &&
            !LayerVisible(layers,
                "Eyes_Normal") &&
            LayerVisible(layers,
                "Sweat_On"),
            "Surprised1 did not select LayeredA's expected eye and sweat layers.");

        character.SetAppearance("Normal1,ArmR3");
        character.SetAppearance("Fearful1");
        Assert(LayerVisible(layers, "Arm_Raised") &&
            !LayerVisible(layers, "Arm_Rest"),
            "Layered appearance did not preserve the prior ArmR3 mutation.");

        var canonical = character.Appearance;
        character.SetAppearance("Default,ArmR1");
        character.SetAppearance(canonical);
        Assert(LayerVisible(layers, "Arm_Raised") &&
            LayerVisible(layers,
                "Eyes_Fearful"),
            "Canonical layered appearance did not restore a stable actor state.");

        var firstChange = character.ChangeAppearance("Surprised1", 1);
        await WaitForTransitionAsync((Sprite2D)character.View);
        var secondChange = character.ChangeAppearance("Normal1", 1);
        await firstChange.Completion;
        character.SetOpacity(.4f); // Also exercise a change while inputs are being prepared.
        await WaitForTransitionAsync((Sprite2D)character.View);
        var overlay = character.View.GetChildren().OfType<Sprite2D>().Single();
        Assert(Mathf.IsEqualApprox(overlay.Modulate.A, .4f), "Capture preparation lost concurrent opacity.");
        Assert(character.View.GetChildren().OfType<SubViewport>()
            .SelectMany(viewport => viewport.GetChildren().OfType<Node2D>())
            .SelectMany(offset => offset.GetChildren().OfType<CanvasGroup>()).All(group => group.Visible),
            "Preemption cloned a hidden composite.");
        secondChange.Finish(); await secondChange.Completion;
        Assert(character.Appearance == character.TargetAppearance && LayerVisible(layers,
            "Eyes_Normal"), "Newest layered appearance was not committed.");
        Assert(character.View.GetChildren().ToHashSet().SetEquals(actorChildrenBeforeTransition),
            "Preempted layered appearances retained capture nodes.");
        var cancelledChange = character.ChangeAppearance("Surprised1", 1);
        character.SetAppearance(canonical);
        await AssertThrowsAsync<OperationCanceledException>(() => cancelledChange.Completion);
        Assert(character.Appearance == canonical, "Cancelled layered appearance overwrote an immediate SetAppearance.");
        character.SetOpacity(1);

        var warden = (ICharacterActor)actors.GetOrCreate(new ActorSpawnRequest(
            "Plain",
            ActorFactoryIds.Character,
            "1",
            CharacterPositionPreset.Center));
        Assert(warden.FactoryId == ActorFactoryIds.Character,
            "Plain should use the ordinary character factory.");
        Assert(((Sprite2D)warden.View).Texture?.GetSize() == new Vector2(16, 24),
            "Plain appearance 1 was not loaded from the synthetic PNG.");

        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(character.SceneNode),
            "Layered character node leaked after runtime shutdown.");
    }

    private static bool LayerVisible(Sprite2D[] layers, string name) =>
        layers.Single(layer => layer.Name == name).Visible;
}

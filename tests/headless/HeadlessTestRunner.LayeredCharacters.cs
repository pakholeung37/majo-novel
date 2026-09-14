#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Godot;
using Majo.Demos.Manosaba.Actors.Layered;
using Majo.Runtime.Actors;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private const string TestMultiplyShaderPath = "res://tests/headless/fixtures/shaders/AlphaAwareMultiply.gdshader";
    private const string TestCompositeShaderPath = "res://tests/headless/fixtures/shaders/LayeredComposite.gdshader";

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
        bootstrap.AddPlugin(new LayeredCharacterPlugin(TestMultiplyShaderPath, TestCompositeShaderPath));
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
        Assert(character.FactoryId == LayeredActorIds.Factory,
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

        // Runtime actors are culled separately from backgrounds. Transient overlays
        // must retain the actor's non-default layer in both appearance APIs.
        Majo.Runtime.Direction.Godot.GodotDirectionRenderHost.AssignVisibilityLayer(
            character.View, Majo.Runtime.Direction.Godot.GodotDirectionRenderHost.SubjectVisibilityLayer);
        var actorHost = character.View.GetParent();
        var nodesBeforeTransition = actorHost.GetChildren().ToHashSet();
        var actorChildrenBeforeTransition = character.View.GetChildren().ToHashSet();
        var sourceAppearance = character.Appearance;
        using (var unchanged = await ((IActorAppearanceTarget)character).PrepareAppearanceTransitionAsync(
            sourceAppearance, "Normal1", new AnimationSpec(.2), new DissolveTransitionDriver()))
        {
            Assert(character.View.GetChildren().ToHashSet().SetEquals(actorChildrenBeforeTransition),
                "Equivalent alias preparation created visual nodes.");
            unchanged.Commit();
            unchanged.ApplyProgress(.5f);
            Assert(layerComposite.Visible && character.Appearance == sourceAppearance &&
                character.View.GetChildren().ToHashSet().SetEquals(actorChildrenBeforeTransition),
                "Equivalent dissolve must keep the stable actor visible without an overlay.");
            unchanged.Finish();
            unchanged.Finish();
        }
        using (var abandoned = await ((IActorAppearanceTarget)character).PrepareAppearanceTransitionAsync(
            sourceAppearance, "Default", new AnimationSpec(.2), new DissolveTransitionDriver()))
        {
            abandoned.Dispose();
            Assert(character.Appearance == sourceAppearance, "Abandoning equivalent preparation changed appearance.");
            await AssertThrowsAsync<InvalidOperationException>(() => { abandoned.Commit(); return Task.CompletedTask; });
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => ((IActorAppearanceTarget)character)
                .PrepareAppearanceTransitionAsync(sourceAppearance, "Default", new AnimationSpec(.2),
                    new DissolveTransitionDriver(), cancelled.Token));
        }
        // Fade intentionally hides even identical input; a custom dissolve may do so too.
        using var customShader = GD.Load<Shader>("res://addons/majo_runtime/Runtime/Direction/Shaders/DissolveTransition.gdshader");
        foreach (var driver in new ITransitionDriver[] { new FadeTransitionDriver(), new DissolveTransitionDriver(customShader) })
        using (var effect = await ((IActorAppearanceTarget)character).PrepareAppearanceTransitionAsync(
            character.Appearance, "Default", new AnimationSpec(.2), driver))
        {
            effect.Commit();
            Assert(character.View.GetChildren().OfType<Sprite2D>().Any(), "Non-identity driver was incorrectly elided.");
            effect.Finish();
        }
        using (var prepared = await ((IActorAppearanceTarget)character).PrepareAppearanceTransitionAsync(
            character.Appearance, "Surprised1", new AnimationSpec(.2),
            new Majo.Runtime.Direction.Transitions.DissolveTransitionDriver()))
        {
            await VerifySparseLayeredInputsAsync(character, layerComposite, "Surprised1");
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
        // ProcessFrame starts before QueueFree is flushed; cross a complete frame before checking cleanup.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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

        Assert(character.Appearance == canonical && character.View.GetChildren().ToHashSet().SetEquals(actorChildrenBeforeTransition),
            "Layered canonical appearance did not remain stable after prepared transitions.");
        character.SetOpacity(1);

        var warden = (ICharacterActor)await actors.GetOrCreateAsync(new ActorSpawnRequest(
            "Plain",
            ActorFactoryIds.Character,
            "1",
            CharacterPositionPreset.Center));
        Assert(warden.FactoryId == ActorFactoryIds.Character,
            "Plain should use the ordinary character factory.");
        Assert(((Sprite2D)warden.View).Texture?.GetSize() == new Vector2(16, 24),
            "Plain appearance 1 was not loaded from the synthetic PNG.");

        await VerifyUnchangedAppearanceTimingAsync(bootstrap);
        await bootstrap.RequestShutdownAsync();
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(character.SceneNode),
            "Layered character node leaked after runtime shutdown.");
    }

    private async Task VerifyUnchangedAppearanceTimingAsync(MajoRuntimeBootstrap runtime)
    {
        var coordinator = runtime.Host!.Services.Get<IDirectionTransactionCoordinator>();
        var id = new ActorId("LayeredA");
        ActorPatch Appearance(string value, double duration) => ActorPatch.Place(StageId.Main, id) with
        {
            Appearance = DirectionValue.From<string?>(value),
            Transition = duration == 0 ? DirectionTransitionSpec.Cut : new(DirectionTransitionId.Dissolve, duration),
        };
        await coordinator.ExecuteAsync(new[] { Appearance("Default", 0) }, DirectionCompletionBarrier.Settle);
        var unchanged = await coordinator.ExecuteAsync(new[] { Appearance("Normal1", .2) });
        Assert(!unchanged.Settlement.IsCompleted, "Equivalent appearance discarded the authored duration.");
        await ToSignal(GetTree().CreateTimer(.04), SceneTreeTimer.SignalName.Timeout);
        Assert(!unchanged.Settlement.IsCompleted, "Equivalent appearance settled before its duration.");
        await unchanged.Settlement;
        var old = await coordinator.ExecuteAsync(new[] { Appearance("Surprised1", .5) });
        var replacement = await coordinator.ExecuteAsync(new[] { Appearance("Surprised1", .2) });
        await old.Settlement;
        Assert(!replacement.Settlement.IsCompleted, "Replacing an active appearance lost its new clock.");
        await coordinator.FinishAsync(replacement);
        Assert(replacement.Settlement.IsCompletedSuccessfully, "Finish failed to settle equivalent appearance.");

        // Roll back a committed no-input appearance using the same component protocol
        // as a failed composite transaction, then check a following real change.
        var before = coordinator.State;
        var plan = new DirectionPlanner().Plan(before, new[] { Appearance("Surprised1", .2) }).DomainPlan;
        var backend = runtime.Host.Services.Get<GodotDirectionStageBackend>();
        await using (var prepared = await backend.PrepareAsync(plan, StageId.Main,
            before.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main], default))
        {
            var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
            prepared.Commit(token);
            prepared.Rollback(token);
        }
        await coordinator.ExecuteAsync(new[] { Appearance("Default", .05) }, DirectionCompletionBarrier.Settle);
    }

    private async Task ValidateRealLayeredInputsAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Real layered pixel comparison requires a renderer.");
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://demos/manosaba/assets";
        var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
        runtime.AddPlugin(new LayeredCharacterPlugin());
        AddChild(runtime);
        await runtime.InitializationCompletion;
        try
        {
            var actors = runtime.Host!.Services.Get<IActorManager>();
            foreach (var (id, appearance) in new[]
            {
                ("Meruru", "Normal1,ArmR8_1,ArmL8,Eyes1_Normal_Open2,Mouth1_Normal_Open2"),
                ("Meruru", "Normal2,ArmR9_2,ArmL9,Eyes2_Smile_Closed1,Mouth2_Smile_Closed1"),
                ("Ema", "Surprised1,Angle01/Head01/Facial01/Mouth01~Mouth01_Pensive_Open"),
            })
            {
                var actor = (ICharacterActor)await actors.GetOrCreateAsync(new ActorSpawnRequest(id,
                    ActorFactoryIds.Character, "Default", CharacterPositionPreset.Center));
                actor.SetVisible(true);
                actor.SetOpacity(.4f);
                using var prepared = await ((IActorAppearanceTarget)actor).PrepareAppearanceTransitionAsync(
                    actor.Appearance, appearance, new AnimationSpec(.2), new DissolveTransitionDriver());
                await VerifySparseLayeredInputsAsync(actor, actor.View.GetNode<CanvasGroup>("LayerComposite"), appearance);
                prepared.Commit();
                prepared.ApplyProgress(.5f);
                prepared.Finish();
                GD.Print($"Full/sparse real layered pixel comparison passed: {id} {appearance}");
            }
        }
        finally
        {
            await runtime.RequestShutdownAsync();
            runtime.QueueFree();
        }
    }

    private async Task VerifySparseLayeredInputsAsync(ICharacterActor character, CanvasGroup stable, string target)
    {
        var pool = GodotActorCapturePool.For(this);
        var inputs = pool.GetChildren().OfType<SubViewport>()
            .Where(v => v.RenderTargetUpdateMode == SubViewport.UpdateMode.Always && v.GetChildCount() > 0).ToArray();
        Assert(inputs.Length == 2, "Expected exactly two live appearance inputs.");
        var original = character.Appearance;
        foreach (var input in inputs)
        {
            var offset = input.GetChild<Node2D>(0);
            var sparse = offset.GetChild<CanvasGroup>(0);
            if (input.Name == "AppearanceTarget") character.SetAppearance(target);
            else character.SetAppearance(original);
            var selected = stable.GetChildren().OfType<Sprite2D>().Where(v => v.Visible).Select(v => v.Name.ToString()).ToArray();
            Assert(sparse.GetChildCount() < stable.GetChildCount() &&
                sparse.GetChildren().Select(v => v.Name.ToString()).SequenceEqual(selected),
                "Sparse input must contain precisely the selected sprites in render order.");
            if (DisplayServer.GetName() == "headless") continue;
            // Oracle: the former full-tree clone, including hidden sprites and the
            // stable appearance's mask materials. Compare actual rendered pixels.
            var full = (CanvasGroup)stable.Duplicate();
            full.SetInstanceShaderParameter("group_opacity", 1f);
            var comparison = pool.Acquire(full, (CanvasGroup)sparse.Duplicate(),
                new Rect2(-offset.Position, input.Size2DOverride));
            try
            {
                using var expected = await ReadTransitionFrameAsync(comparison.Source);
                using var actual = await ReadTransitionFrameAsync(comparison.Target);
                Assert(expected.GetData().SequenceEqual(actual.GetData()),
                    "Sparse layered input changed pixels compared with the full-tree renderer.");
            }
            finally { comparison.Release(); }
        }
        character.SetAppearance(original);
    }

    private static bool LayerVisible(Sprite2D[] layers, string name) =>
        layers.Single(layer => layer.Name == name).Visible;
}

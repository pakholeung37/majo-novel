#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private static readonly string[] SlotActorIds = { "a", "b" };

    private async Task ValidateActorSlotsAsync()
    {
        var owner = new Node { Name = "ActorSlotsProbe" };
        AddChild(owner);
        var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        var clock = new GodotFrameClock(GetTree());
        await clock.InitializeAsync(CancellationToken.None);
        var runner = new OperationRunner(clock, new HeadlessDirectionRuntimeLifetime());
        await runner.InitializeAsync(CancellationToken.None);
        using var textures = new ResourceActorTextureLoader(failOnMissingAssets: true);
        using var config = new ActorConfiguration { ReferenceResolution = new Vector2I(640, 360) };
        var definitions = SlotActorIds.Select(id => new ActorDefinition(id, ActorFactoryIds.Character,
            new TextureActorDefinitionData(id, "characters", "Default", new Dictionary<string, string>
            {
                ["Default"] = "res://tests/headless/fixtures/character_red.svg",
                ["blue"] = "res://tests/headless/fixtures/character_blue.svg",
            }))).ToArray();
        using var resourceConfig = new ResourceConfiguration { RootPath = "res://tests/headless/fixtures", ObjectsPath = "objects" };
        IActorManager Create(Node2D root)
        {
            var backgroundRoot = new Control();
            root.AddChild(backgroundRoot);
            return new ActorManager(definitions,
            new IActorFactory[] { new CharacterActorFactory(root, textures, new CharacterPositionMap(config)),
                new SceneObjectActorFactory(root, textures, ActorResourceSettings.Capture(resourceConfig), config.ReferenceResolution),
                new BackgroundActorFactory(backgroundRoot, textures) });
        }
        var stage = new GodotDirectionStageBackend(host, new HeadlessDirectionTextureLoader(), Create);
        var backend = new CompositeDirectionPreparationBackend(stage, new GodotDirectionViewBackend(host),
            new GodotDirectionScreenBackend(host), new GodotDirectionEffectBackend(host));
        var accessibility = new DirectionAccessibility(false);
        var actorModifiers = new ActorModifierManager(host.FindActorModifierTarget, runner, accessibility, clock);
        host.ReleaseActorEffectsAsync = target => actorModifiers.ClearTargetAsync(target);
        var cameraModifiers = new CameraModifierManager(host.ApplyViewModifierDelta, host.ClearViewModifierDelta,
            runner, accessibility, clock);
        await cameraModifiers.InitializeAsync(CancellationToken.None);
        var services = new RuntimeServiceRegistry().AddSingleton<IActorModifierManager>(_ => actorModifiers).Build();
        await services.InitializeAsync();
        var cues = new TransientDirectionCueExecutor(new TransientCueHandlerRegistry(), cameraModifiers, services.Services);
        using var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend, cues: cues,
            clearSceneRendering: async token =>
            {
                await actorModifiers.ResetAsync(token); await cameraModifiers.ResetAsync(token);
                await stage.ResetAsync(token); await host.ResetSceneAsync(token);
            });
        var a = new ActorId("a");
        var b = new ActorId("b");
        ActorPatch Place(ActorId id) => ActorPatch.Place(StageId.Main, id);
        ActorMovePatch Move(ActorId id, float x, double duration = .3) => new(StageId.Main, id,
            DirectionActorKind.Character, DirectionValue.From(new DirectionPoint(x, 0)), default, default,
            new DirectionTransitionSpec(DirectionTransitionId.Move, duration));
        var surface = host.GetOrCreateStage(StageId.Main);
        Sprite2D View(string id) => surface.Actors.GetChildren().OfType<Node2D>()
            .SelectMany(root => root.GetChildren().OfType<Sprite2D>()).Single(node => node.Name == id);
        try
        {
            await coordinator.ExecuteAsync(new[] { Place(a), Place(b) });
            var rootA = View("a");
            var rootB = View("b");
            var walk = await coordinator.ExecuteAsync(new[] { Move(b, 90, 1) });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var expression = await coordinator.ExecuteAsync(new[] { Place(a) with
            {
                Appearance = DirectionValue.From<string?>("blue"),
                Transition = new DirectionTransitionSpec(DirectionTransitionId.Dissolve, .05),
            } });
            Assert(ReferenceEquals(rootA, View("a")) && ReferenceEquals(rootB, View("b")),
                "Appearance publication must preserve both stable actor roots.");
            Assert(!walk.Settlement.IsCompleted, "Another actor appearance must not finish existing movement.");
            await expression.Settlement;
            var sampled = rootB.Position;
            var rotation = await coordinator.ExecuteAsync(new[] { Move(b, 0, .05) with
            { Position = default, RotationDegrees = DirectionValue.From(30f) } });
            await walk.Settlement;
            await rotation.Settlement;
            Assert(rootB.Position.IsEqualApprox(sampled), "An omitted position must freeze at the replacement commit.");
            Assert(Math.Abs(coordinator.State.Stages[StageId.Main].Actors[b].Position.X - sampled.X / 640f * 100) < .01,
                "The frozen position must be written to authoritative state.");
            var hide = await coordinator.ExecuteAsync(new[] { new ActorHidePatch(StageId.Main, a,
                DirectionActorKind.Character, new DirectionTransitionSpec(DirectionTransitionId.Fade, .2)) });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var show = await coordinator.ExecuteAsync(new[] { Place(a) with
            { Transition = new DirectionTransitionSpec(DirectionTransitionId.Fade, .05) } });
            await hide.Settlement;
            await show.Settlement;
            Assert(ReferenceEquals(rootA, View("a")), "Show must supersede hide without old callback destruction.");
            await coordinator.ExecuteAsync(new[] { new ActorHidePatch(StageId.Main, a,
                DirectionActorKind.Character, DirectionTransitionSpec.Cut) }, DirectionCompletionBarrier.Settle);
            Assert(!coordinator.State.Stages[StageId.Main].Actors[a].Visible,
                "Completed hide retains a hidden record.");
            await coordinator.ExecuteAsync(new[] { Place(a) });
            Assert(!ReferenceEquals(rootA, View("a")), "Completed hidden actors rebuild presentation when shown.");
            var first = new ActorId("knife1");
            var second = new ActorId("knife2");
            ActorPatch Object(ActorId id, float x) => Place(id) with
            {
                Kind = DirectionActorKind.SceneObject,
                DefinitionId = "evidence",
                Position = DirectionValue.From(new DirectionPoint(x, 30)),
                RotationDegrees = DirectionValue.From(15f),
            };
            await coordinator.ExecuteAsync(new[] { Object(first, -20), Object(second, 120) });
            Assert(View("knife1").Position.IsEqualApprox(new Vector2(-128, 252)) &&
                View("knife2").Position.IsEqualApprox(new Vector2(768, 252)), "Objects use Stage percentages including offscreen positions.");
            Assert(View("knife1").Offset.IsEqualApprox(View("knife1").Texture.GetSize() * new Vector2(.5f, -.5f)),
                "The definition anchor must determine the object sprite offset.");
            var beforeObjectFailure = coordinator.State;
            await AssertThrowsAsync<ActorException>(() => coordinator.ExecuteAsync(new[]
            { Object(new ActorId("missing"), 20) with { DefinitionId = "not-found" } }));
            await AssertThrowsAsync<DirectionException>(() => coordinator.ExecuteAsync(new[]
            { Object(new ActorId("unsupported"), 20) with { DefinitionId = "unsupported" } }));
            Assert(ReferenceEquals(beforeObjectFailure, coordinator.State), "Missing Object assets or capabilities must fail without committing state.");
            var objectRoot = View("knife1");
            var fxTarget = new ActorFxTarget(StageId.Main, ActorFxSlot.Instance, first);
            var shake = await coordinator.ExecuteAsync(new[] { new ActorFxCue(fxTarget, DirectionActorKind.SceneObject, "shake", 2) });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(objectRoot.Position.IsEqualApprox(new Vector2(-128, 252)), "FX must not write base object coordinates.");
            var hideObject = await coordinator.ExecuteAsync(new IDirectionDirective[]
            {
                new ActorMovePatch(StageId.Main, first, DirectionActorKind.SceneObject,
                    DirectionValue.From(new DirectionPoint(50, 30)), default, default,
                    new DirectionTransitionSpec(DirectionTransitionId.Move, 1)),
                new ActorHidePatch(StageId.Main, first, DirectionActorKind.SceneObject, DirectionTransitionSpec.Cut),
                new ActorFxCue(fxTarget, DirectionActorKind.SceneObject, "shake", 1),
            }, DirectionCompletionBarrier.Settle);
            await shake.Settlement;
            Assert(host.FindActorModifierTarget(fxTarget) is null && !coordinator.State.Stages[StageId.Main].Actors[first].Visible,
                "A zero-duration hide must finish base motion and release every overlapping FX.");
            var revision = coordinator.State.Revision;
            try
            {
                await coordinator.ExecuteAsync(new[] { new ActorFxCue(new ActorFxTarget(StageId.Main, ActorFxSlot.Instance, second),
                    DirectionActorKind.SceneObject, "missing") });
                throw new InvalidOperationException("Unknown FX must fail validation.");
            }
            catch (DirectionException) { Assert(coordinator.State.Revision == revision, "Invalid FX must fail before commit."); }
            var cameraWalk = await coordinator.ExecuteAsync(new[] { new ViewPosePatch(ViewId.Main,
                DirectionValue.From(new DirectionPoint(80, 60)), DirectionValue.From(2f), default,
                new DirectionTransitionSpec(DirectionTransitionId.Move, 1)) });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var cameraRotate = await coordinator.ExecuteAsync(new[] { new ViewPosePatch(ViewId.Main,
                default, default, DirectionValue.From(30f), new DirectionTransitionSpec(DirectionTransitionId.Move, .05)) });
            var frozenCamera = coordinator.State.Views[ViewId.Main].Pose;
            await cameraWalk.Settlement;
            await cameraRotate.Settlement;
            Assert(host.GetCurrentViewPose(ViewId.Main) == frozenCamera && frozenCamera.Center.X < 80 && frozenCamera.Zoom < 2,
                "Camera replacement must sample the full current pose at commit and freeze omitted fields.");
            var extra = new StageId("extra");
            await coordinator.ExecuteAsync(new IDirectionDirective[]
            {
                new StageCreatePatch(extra, new ViewId("extra"), DirectionTransitionSpec.Cut),
            });
            var extraBackdropTarget = new ActorFxTarget(extra, ActorFxSlot.Backdrop);
            BackdropPatch Background(string name, double duration = 0) => new(extra,
                new BackdropState(new ResourceKey($"tests/headless/fixtures/{name}.svg")),
                duration == 0 ? DirectionTransitionSpec.Cut : new DirectionTransitionSpec(DirectionTransitionId.Dissolve, duration));
            await coordinator.ExecuteAsync(new[] { Background("background_dark") });
            var backgroundShake = await coordinator.ExecuteAsync(new[] { new ActorFxCue(extraBackdropTarget,
                DirectionActorKind.Character, "shake", 2, Axis: ActorShakeAxis.X) });
            var backdropMix = await coordinator.ExecuteAsync(new[] { Background("background_light", 1) });
            await coordinator.ExecuteAsync(new[] { Background("background_light") });
            await backdropMix.Settlement;
            Assert(!backgroundShake.Settlement.IsCompleted && host.FindActorModifierTarget(extraBackdropTarget) is not null,
                "Backdrop replacement must complete the old mix without stopping slot FX.");
            await coordinator.ExecuteAsync(new[] { new StageClearPatch(extra, DirectionTransitionSpec.Cut) }, DirectionCompletionBarrier.Settle);
            await backgroundShake.Settlement;
            Assert(host.FindActorModifierTarget(extraBackdropTarget) is null && coordinator.State.Views[ViewId.Main].Pose == frozenCamera,
                "Stage clear must remove backdrop FX and preserve camera state.");
            var curtainNode = host.Curtain;
            var mixed = await coordinator.ExecuteAsync(new IDirectionDirective[]
            {
                Move(a, 80, 1),
                new CurtainPatch(new CurtainState(DirectionColor.Black, 1),
                    new DirectionTransitionSpec(DirectionTransitionId.Fade, 10)),
            });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var stoppedAudio = false;
            await coordinator.ClearSceneAsync(new DirectionSceneContext("fixture", "second", 1),
                _ => { stoppedAudio = true; return Task.CompletedTask; });
            Assert(stoppedAudio && coordinator.State.Stages.Count == 1 && coordinator.State.Views.Count == 1,
                "Scene boundary must stop owned audio and retain only empty main.");
            Assert(coordinator.State.Stages[StageId.Main].Actors.Count == 0 && !host.HasStage(extra),
                "Scene boundary must release old actor records and extra surfaces.");
            Assert(ReferenceEquals(curtainNode, host.Curtain) && !mixed.Settlement.IsCompleted && host.Curtain.Color.A < .5f,
                "A scene boundary must preserve a global Curtain operation from the same beat.");
            await coordinator.FinishAsync(mixed);

        }
        finally
        {
            await coordinator.ResetAsync();
            await actorModifiers.ResetAsync(CancellationToken.None);
            await cameraModifiers.ResetAsync(CancellationToken.None);
            await stage.ResetAsync(CancellationToken.None);
            await host.ShutdownAsync(CancellationToken.None);
            await runner.ShutdownAsync(CancellationToken.None);
            await clock.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }
    }
}

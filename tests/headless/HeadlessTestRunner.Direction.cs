#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateDirectionRenderingAsync()
    {
        using (var configuration = new AnimationConfiguration
        {
            DefaultBackdropAnimationId = "dissolve",
            DefaultCharacterAnimationId = "move",
            DefaultDirectionEasingId = "sine-out",
        })
        {
            Assert(configuration.ResolveBackdropAnimationId() == DirectionTransitionId.Dissolve,
                "Configured backdrop ID should match.");
            Assert(configuration.ResolveCharacterAnimationId() == DirectionTransitionId.Move,
                "Configured character ID should match.");
            Assert(configuration.ResolveDirectionEasingId() == EasingId.SineOut,
                "Configured easing ID should match.");
            configuration.DefaultBackdropAnimationId = "fade";
            Assert(configuration.ResolveBackdropAnimationId() == DirectionTransitionId.Fade,
                "Explicit new configuration must update resolution.");
        }
        var owner = new Node { Name = "DirectionRenderingProbe" };
        AddChild(owner);
        var customEasings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(customEasings);
        customEasings.Register(new EasingDefinition("square", t => t * t));

        foreach (var driver in new ITransitionDriver[] { new FadeTransitionDriver(), new DissolveTransitionDriver() })
        {
            var surface = new Sprite2D();
            owner.AddChild(surface);
            var easing = customEasings.GetRequired(new EasingId("square"));
            using var inputImage = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            using var inputTexture = ImageTexture.CreateFromImage(inputImage);
            var prepared = driver.Prepare(new VisualTransitionContext(
                surface,
                new DirectionTransitionSpec(driver.Id, 1, new EasingId("square")), inputTexture, inputTexture));
            var tween = owner.CreateTween();
            tween.Pause();
            tween.TweenMethod(
                Callable.From<double>(progress => prepared.ApplyProgress((float)easing.Evaluate(progress))),
                0.0f,
                1.0f,
                1.0);
            tween.CustomStep(0.5);
            if (surface.Material is ShaderMaterial mat)
            {
                Assert(Math.Abs((float)mat.GetShaderParameter("progress") - 0.25f) < 0.001f,
                    "Dissolve driver must apply custom easing to its shader progress.");
            }
            else
            {
                Assert(Math.Abs(surface.Modulate.A - 0.75f) < 0.001f,
                    "Transition drivers must apply custom easing to their progress.");
            }
            tween.Kill();
            prepared.Dispose();
            surface.Free();
        }
        var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        var textures = new HeadlessDirectionTextureLoader();
        var actorTextureLoader = new ResourceActorTextureLoader(failOnMissingAssets: true);
        var actorConfiguration = new ActorConfiguration
        {
            ReferenceResolution = new Vector2I(640, 360),
        };
        var actorDefinitions = new[]
        {
            new ActorDefinition(
                "hero",
                ActorFactoryIds.Character,
                "red",
                new Dictionary<string, string>
                {
                    ["red"] = "res://tests/headless/fixtures/character_red.svg",
                    ["blue"] = "res://tests/headless/fixtures/character_blue.svg",
                }),
        };
        var actorLifetime = new HeadlessDirectionRuntimeLifetime();
        var actorFrameClock = new GodotFrameClock(GetTree());
        await actorFrameClock.InitializeAsync(CancellationToken.None);
        var actorScheduler = new OperationRunner(actorFrameClock, actorLifetime);
        await actorScheduler.InitializeAsync(CancellationToken.None);
        var cameraModifiers = new CameraModifierManager(
            (viewId, delta) => host.ApplyViewModifierDelta(viewId, delta),
            viewId => host.ClearViewModifierDelta(viewId),
            actorScheduler,
            new DirectionAccessibility(),
            actorFrameClock);
        await cameraModifiers.InitializeAsync(CancellationToken.None);
        IActorManager CreateActorManager(Node2D actorRoot) => new ActorManager(
            actorDefinitions,
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    actorRoot,
                    actorScheduler,
                    actorTextureLoader,
                    new CharacterPositionMap(actorConfiguration),
                    customEasings),
            });
        var actorDrivers = new TransitionDriverRegistry();
        var customActorDriver = new RecordingTransitionDriver();
        actorDrivers.Register(customActorDriver);
        var stageBackend = new GodotDirectionStageBackend(
            host,
            textures,
            CreateActorManager,
            drivers: actorDrivers, easings: customEasings);
        var composite = new CompositeDirectionPreparationBackend(
            stageBackend,
            new GodotDirectionViewBackend(host, easings: customEasings),
            new GodotDirectionScreenBackend(host, easings: customEasings),
            new GodotDirectionEffectBackend(host, easings: customEasings));


        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            composite);

        var backdropKey = new ResourceKey("tests/headless/fixtures/background_dark.svg");
        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(backdropKey),
                    DirectionTransitionSpec.Cut),
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(25, 0),
                    "red",
                    1.25f,
                    7),
            },
            DirectionCompletionBarrier.Settle);

        Assert(host.PublishedBackdrop?.Resource == backdropKey,
            "Direction Backdrop did not publish its prepared resource.");
        Assert(host.Backdrop.Texture is not null,
            "Direction Backdrop committed without a Texture2D.");
        var stageBackdropTexture = host.Backdrop.Texture;
        Assert(host.PublishedView?.StageId == StageId.Main,
            "Default Direction View stopped observing main Stage.");
        var firstActorRoot = GetVisibleActorRoot(host);
        var firstActor = firstActorRoot.GetNode<Sprite2D>("hero");
        Assert(firstActor.Position.IsEqualApprox(new Vector2(160, 360)),
            "Direction Actor did not convert scene percentages into Stage coordinates.");
        Assert(firstActor.Scale.IsEqualApprox(new Vector2(1.25f, 1.25f)) &&
                firstActor.ZIndex == 7 && firstActor.Texture is not null,
            "Direction Actor did not commit appearance, scale, and z-index together.");

        var revisionBeforeUnsupportedEase = coordinator.State.Revision;
        var rejectedCustomActorEase = false;
        try
        {
            await coordinator.ExecuteAsync(new IDirectionDirective[]
            {
                ActorPatchFor("hero", new DirectionPoint(30, 0), "red", 1.25f, 7) with
                {
                    Transition = new DirectionTransitionSpec(DirectionTransitionId.Move, 1, new EasingId("unregistered_ease")),
                },
            });
        }
        catch (DirectionException exception)
        {
            rejectedCustomActorEase = exception.Message.Contains("unregistered_ease", StringComparison.Ordinal);
        }
        Assert(rejectedCustomActorEase && coordinator.State.Revision == revisionBeforeUnsupportedEase,
            "Unsupported actor easing must fail preparation without publishing state.");




        var illustration = new StageId("illustration");
        var illustrationView = new ViewId("illustration");
        var illustrationKey = new ResourceKey("tests/headless/fixtures/background_light.svg");
        var illustrationPose = new ViewPose(new DirectionPoint(37.384f, 74.816f), 3, 10);
        var illustrationTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new StageCreatePatch(
                    illustration,
                    illustrationView,
                    DirectionTransitionSpec.Cut),
                new BackdropPatch(
                    illustration,
                    new BackdropState(illustrationKey),
                    DirectionTransitionSpec.Cut),
                ViewPoseFor(illustrationView, illustrationPose, DirectionTransitionSpec.Cut),
                ShowView(
                    illustrationView,
                    new DirectionTransitionSpec(
                        DirectionTransitionId.Dissolve,
                        2,
                        EasingId.SineInOut)),
            });

        Assert(coordinator.State.Screen.Bindings[LayoutSlotId.Main] == illustrationView,
            "Illustration Stage did not become the Screen's active View at Commit.");
        Assert(host.ScreenTransitionSource.Visible &&
                host.ScreenTransitionSource.Texture is ImageTexture,
            "Direction Screen dissolve did not retain a capture of the previous Screen frame.");
        Assert(Equals(coordinator.State.Views[illustrationView].Pose, illustrationPose),
            "Illustration View Pose was not committed atomically.");
        Assert(ReferenceEquals(stageBackdropTexture, host.Backdrop.Texture),
            "Illustration Stage mutation replaced the main Stage Backdrop texture.");
        Assert(host.PublishedBackdrop?.Resource == backdropKey,
            "Illustration Stage mutation changed the main Stage Backdrop state.");

        await coordinator.FinishAsync(illustrationTransaction);
        Assert(host.PublishedScreen.Bindings[LayoutSlotId.Main] == illustrationView,
            "Finishing the illustration transition did not retain its Screen binding.");
        Assert(!host.ScreenTransitionSource.Visible &&
                host.ScreenTransitionSource.Texture is null,
            "Finishing the Screen dissolve retained its previous-frame overlay.");

        var illustrationSurface = host.RequireView(illustrationView);
        var stableIllustrationTransform = illustrationSurface.Transform.Transform;
        var shake = cameraModifiers.ApplyEffect(new CameraFxCue(
            illustrationView,
            "shake",
            new Dictionary<string, string>
            {
                ["strength"] = "20",
                ["axis"] = "x",
                ["frequency"] = "20",
            },
            1));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(Mathf.Abs(illustrationSurface.ModifierDelta.CenterOffset.X) > 0.01f &&
                Mathf.IsZeroApprox(illustrationSurface.ModifierDelta.CenterOffset.Y) &&
                !illustrationSurface.Transform.Transform.IsEqualApprox(stableIllustrationTransform),
            "Direction camera shake did not offset the targeted non-main View.");
        shake.Finish();
        await shake.Completion;
        Assert(illustrationSurface.ModifierDelta.CenterOffset.IsZeroApprox() &&
                illustrationSurface.Transform.Transform.IsEqualApprox(stableIllustrationTransform),
            "Direction camera shake did not restore the targeted View's stable pose.");

        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ShowView(ViewId.Main, DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);

        Assert(host.PublishedScreen.Bindings[LayoutSlotId.Main] == ViewId.Main,
            "Direction Screen did not return from the illustration Stage to main.");
        Assert(ReferenceEquals(stageBackdropTexture, host.Backdrop.Texture),
            "Returning from the illustration rebuilt or replaced the stable main Backdrop.");

        var cutPose = new ViewPose(
            new DirectionPoint(37.384f, 74.816f),
            3,
            10);
        var movePose = new ViewPose(
            new DirectionPoint(44.438f, 71.950f),
            3,
            10);
        var sequenceTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ViewPoseFor(ViewId.Main, cutPose, DirectionTransitionSpec.Cut),
                new ViewPosePatch(
                    ViewId.Main,
                    DirectionValue.From(movePose.Center),
                    default,
                    default,
                    new DirectionTransitionSpec(
                        DirectionTransitionId.Move,
                        0.4,
                        EasingId.SineOut)),
            });
        Assert(Equals(host.PublishedView?.Pose, cutPose),
            "A sequential View move did not publish its Cut pose before tweening.");
        var poseAtCommit = host.ViewTransform.Transform;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var poseDuringMove = host.ViewTransform.Transform;
        Assert(poseAtCommit.Origin.DistanceTo(poseDuringMove.Origin) > 0.01f,
            "A sequential View move did not animate after its Cut pose.");
        await coordinator.FinishAsync(sequenceTransaction);
        Assert(Equals(host.PublishedView?.Pose, movePose),
            "A sequential View move did not publish its final pose after settlement.");

        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(75, 10),
                    "blue",
                    0.8f,
                    3),
            },
            DirectionCompletionBarrier.Settle);
        var secondActorRoot = GetVisibleActorRoot(host);
        var secondActor = secondActorRoot.GetNode<Sprite2D>("hero");
        Assert(!ReferenceEquals(firstActorRoot, secondActorRoot) && !firstActorRoot.Visible,
            "Direction Actor Commit mutated the old actor tree instead of swapping roots.");
        Assert(secondActor.Position.IsEqualApprox(new Vector2(480, 324)) &&
                secondActor.Scale.IsEqualApprox(new Vector2(0.8f, 0.8f)) &&
                secondActor.ZIndex == 3,
            "Prepared Direction Actor tree did not publish its complete target state.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(firstActorRoot),
            "Direction Actor root swap retained the replaced tree after settlement.");

        var moveTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(50, 0),
                    "red",
                    1,
                    0,
                    new DirectionTransitionSpec(DirectionTransitionId.Move, 0.2, new EasingId("square"))),

            });
        var movedActorRoot = GetVisibleActorRoot(host);
        var movedActor = movedActorRoot.GetNode<Sprite2D>("hero");
        Assert(!secondActorRoot.Visible &&
                movedActor.Position.IsEqualApprox(secondActor.Position),
            "Direction move did not begin from the previous stable Actor position.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(movedActor.Position.X is < 480 and > 320,
            "Direction move did not expose an intermediate Actor position.");
        await coordinator.FinishAsync(moveTransaction);
        Assert(movedActor.Position.IsEqualApprox(new Vector2(320, 360)),
            "Finishing Direction move did not apply its exact target position.");

        var fadeTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(50, 0),
                    "blue",
                    1,
                    0,
                    new DirectionTransitionSpec(customActorDriver.Id, 1.0)),
            });
        var fadedActorRoot = GetLatestVisibleActorRoot(host);
        var fadedActor = fadedActorRoot.GetNode<Sprite2D>("hero");
        Assert(!movedActorRoot.Visible && (fadedActorRoot.GetChildCount() > 1 || fadedActor.GetChildCount() > 0),
            "Direction dissolve did not publish the target Actor tree.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert((fadedActorRoot.GetChildCount() > 1 || fadedActor.GetChildCount() > 0) && !movedActorRoot.Visible,
            "Direction dissolve did not delegate to the Actor appearance transition.");
        await coordinator.FinishAsync(fadeTransaction);
        Assert(Mathf.IsEqualApprox(fadedActor.Modulate.A, 1) &&
                (!IsInstanceValid(movedActorRoot) || !movedActorRoot.Visible),
            "Finishing Direction dissolve did not publish only the target Actor visual.");

        Assert(customActorDriver.Preparations == 1, "Actor appearance did not execute the registered driver.");
        await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            ActorPatchFor("hero", new DirectionPoint(50, 0), "blue", 1.1f, 0,
                new DirectionTransitionSpec(customActorDriver.Id, .05)),
        }, DirectionCompletionBarrier.Settle);
        Assert(customActorDriver.Preparations == 2, "Actor visual changes bypassed the registered driver.");
        var actorRootBeforeFailure = GetVisibleActorRoot(host);

        var revisionBeforeFailure = coordinator.State.Revision;
        var backdropBeforeFailure = host.PublishedBackdrop;
        _ = await AssertThrowsAsync<ActorException>(() => coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(illustrationKey),
                    DirectionTransitionSpec.Cut),
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(50, 0),
                    "missing",
                    1,
                    0),
            },
            DirectionCompletionBarrier.Settle));
        Assert(coordinator.State.Revision == revisionBeforeFailure &&
                Equals(host.PublishedBackdrop, backdropBeforeFailure) &&
                ReferenceEquals(GetVisibleActorRoot(host), actorRootBeforeFailure),
            "Failed Actor Prepare partially published Stage state.");

        _ = await AssertThrowsAsync<DirectionException>(() => coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new ActorLeavePatch(
                    StageId.Main,
                    new ActorId("hero"),
                    new DirectionTransitionSpec(DirectionTransitionId.Move, 0.2)),
            },
            DirectionCompletionBarrier.Settle));
        Assert(ReferenceEquals(GetVisibleActorRoot(host), actorRootBeforeFailure),
            "Rejected move-leave changed the stable Actor tree.");

        var leaveTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new ActorLeavePatch(
                    StageId.Main,
                    new ActorId("hero"),
                    new DirectionTransitionSpec(DirectionTransitionId.Fade, 0.2)),
            });
        Assert(actorRootBeforeFailure.Visible && GetLatestVisibleActorRoot(host).GetChildCount() == 0,
            "Direction fade-leave did not retain its source Actor during transition.");
        await coordinator.FinishAsync(leaveTransaction);
        Assert(GetVisibleActorRoot(host).GetChildCount() == 0,
            "Direction leave did not publish an empty target actor tree.");

        var broadcast = new StageId("broadcast");
        var broadcastView = new ViewId("broadcast");
        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new StageCreatePatch(
                    broadcast,
                    broadcastView,
                    DirectionTransitionSpec.Cut),
                new BackdropPatch(
                    broadcast,
                    new BackdropState(illustrationKey),
                    DirectionTransitionSpec.Cut),
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(75, 0),
                    "blue",
                    1,
                    2,
                    stageId: broadcast),
                ShowView(broadcastView, DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);
        var broadcastSurface = host.GetStage(broadcast);
        var broadcastActor = broadcastSurface.Actors.GetChildren()
            .OfType<Node2D>()
            .Single(root => root.Visible)
            .GetNode<Sprite2D>("hero");
        Assert(host.PublishedScreen.Bindings[LayoutSlotId.Main] == broadcastView &&
                broadcastActor.Position.IsEqualApprox(new Vector2(480, 360)),
            "Direction Screen did not switch to the independently prepared Stage View.");

        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(25, 0),
                    "red",
                    1,
                    1),
                ShowView(ViewId.Main, DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);
        Assert(host.PublishedScreen.Bindings[LayoutSlotId.Main] == ViewId.Main &&
                broadcastSurface.Actors.GetChildren()
                    .OfType<Node2D>()
                    .Single(root => root.Visible)
                    .GetNode<Sprite2D>("hero").Position.IsEqualApprox(new Vector2(480, 360)),
            "Returning to main Stage mutated the dormant broadcast Stage.");

        var revisionBeforeMultiStageFailure = coordinator.State.Revision;
        var screenBeforeMultiStageFailure = host.PublishedScreen;
        var failedStage = new StageId("failed");
        _ = await AssertThrowsAsync<ActorException>(() => coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new StageCreatePatch(
                    failedStage,
                    new ViewId("failed"),
                    DirectionTransitionSpec.Cut),
                new BackdropPatch(
                    failedStage,
                    new BackdropState(backdropKey),
                    DirectionTransitionSpec.Cut),
                ActorPatchFor(
                    "hero",
                    new DirectionPoint(50, 0),
                    "missing",
                    1,
                    0,
                    stageId: failedStage),
            },
            DirectionCompletionBarrier.Settle));
        Assert(coordinator.State.Revision == revisionBeforeMultiStageFailure &&
                Equals(host.PublishedScreen, screenBeforeMultiStageFailure) &&
                !host.StageIds.Contains(failedStage),
            "Failed Multi-Stage Prepare partially published state or changed the View.");

        var reaction = new ViewId("reaction");
        var inset = new LayoutSlotId("inset");
        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new ViewCreatePatch(
                    reaction,
                    StageId.Main,
                    DirectionTransitionSpec.Cut),
                ViewPoseFor(
                    reaction,
                    new ViewPose(new DirectionPoint(65, 50), 1.8f, 0),
                    DirectionTransitionSpec.Cut),
                new ScreenPatch(
                    new LayoutId("split"),
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [LayoutSlotId.Main] = ViewId.Main,
                        [inset] = reaction,
                    },
                    DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);
        Assert(host.ViewIds.Contains(reaction) &&
                host.PublishedScreen.Layout == new LayoutId("split") &&
                host.PublishedScreen.Bindings[inset] == reaction,
            "Direction split layout did not publish a second persistent View.");

        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ShowView(ViewId.Main, DirectionTransitionSpec.Cut),
                new ViewReleasePatch(reaction, DirectionTransitionSpec.Cut),
                new StageReleasePatch(broadcast, DirectionTransitionSpec.Cut),
                new StageReleasePatch(illustration, DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);
        Assert(!host.ViewIds.Contains(reaction) &&
                !host.ViewIds.Contains(broadcastView) &&
                !host.ViewIds.Contains(illustrationView) &&
                !host.StageIds.Contains(broadcast) &&
                !host.StageIds.Contains(illustration),
            "Direction Stage/View release retained prepared render surfaces.");

        await stageBackend.ResetAsync(CancellationToken.None);
        await host.ResetAsync(CancellationToken.None);
        Assert(host.PublishedBackdrop is null && host.Backdrop.Texture is null,
            "Direction render host Reset retained its Backdrop.");
        Assert(host.PublishedView?.StageId == StageId.Main,
            "Direction render host Reset did not restore main Stage source.");
        Assert(host.Actors.GetChildCount() == 0,
            "Direction render Reset retained an Actor tree.");

        await stageBackend.ShutdownAsync(CancellationToken.None);
        await cameraModifiers.ShutdownAsync(CancellationToken.None);
        await actorScheduler.ShutdownAsync(CancellationToken.None);
        await actorFrameClock.ShutdownAsync(CancellationToken.None);
        actorLifetime.Dispose();
        await actorTextureLoader.ShutdownAsync(CancellationToken.None);
        await host.ShutdownAsync(CancellationToken.None);
        owner.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(host.StageViewport) &&
                !IsInstanceValid(host.ViewViewport) &&
                !IsInstanceValid(host.ScreenViewport) &&
                !IsInstanceValid(host.OutputLayer),
            "Direction render host leaked viewport or output nodes after Shutdown.");
    }

    private static ViewPosePatch ViewPoseFor(
        ViewId viewId,
        ViewPose pose,
        DirectionTransitionSpec transition) => new(
            viewId,
            DirectionValue.From(pose.Center),
            DirectionValue.From(pose.Zoom),
            DirectionValue.From(pose.RotationDegrees),
            transition);

    private static ScreenPatch ShowView(
        ViewId viewId,
        DirectionTransitionSpec transition) => new(
            LayoutId.SingleLayout,
            new Dictionary<LayoutSlotId, ViewId>
            {
                [LayoutSlotId.Main] = viewId,
            },
            transition);

    private static ActorPatch ActorPatchFor(
        string actorId,
        DirectionPoint position,
        string appearance,
        float scale,
        int z,
        DirectionTransitionSpec? transition = null,
        StageId? stageId = null) => new(
            stageId ?? StageId.Main,
            new ActorId(actorId),
            DirectionValue.From(position),
            DirectionValue.From<string?>(appearance),
            DirectionValue.From(scale),
            DirectionValue.From(z),
            transition ?? DirectionTransitionSpec.Cut);

    private static Node2D GetVisibleActorRoot(GodotDirectionRenderHost host) =>
        host.Actors.GetChildren()
            .OfType<Node2D>()
            .Single(root => root.Visible);

    private static Node2D GetLatestVisibleActorRoot(GodotDirectionRenderHost host) =>
        host.Actors.GetChildren()
            .OfType<Node2D>()
            .Last(root => root.Visible);

    private sealed class HeadlessDirectionTextureLoader : IDirectionTextureLoader
    {
        public Task<Texture2D> LoadAsync(
            ResourceKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"res://{key.Path}";
            return Task.FromResult(
                ResourceLoader.Load<Texture2D>(path) ?? throw new InvalidOperationException(
                    $"Direction fixture texture was not loaded: '{path}'."));
        }
    }

    private sealed class HeadlessDirectionRuntimeLifetime : IRuntimeLifetime, IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();

        public CancellationToken RuntimeCancellationToken => cancellation.Token;
        public CancellationToken SessionCancellationToken => cancellation.Token;

        public void Dispose() => cancellation.Dispose();
    }
}

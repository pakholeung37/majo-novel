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
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateDirectionRenderingAsync()
    {
        var owner = new Node { Name = "DirectionRenderingProbe" };
        AddChild(owner);
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
        var actorScheduler = new OperationScheduler(actorFrameClock, actorLifetime);
        await actorScheduler.InitializeAsync(CancellationToken.None);
        IActorManager CreateActorManager(Node2D actorRoot) => new ActorManager(
            actorDefinitions,
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    actorRoot,
                    actorScheduler,
                    actorTextureLoader,
                    new CharacterPositionMap(actorConfiguration)),
            });
        var stageBackend = new GodotDirectionStageBackend(
            host,
            textures,
            CreateActorManager);
        var composite = new CompositeDirectionPreparationBackend(
            stageBackend,
            new GodotDirectionViewBackend(host, textures),
            new GodotDirectionScreenBackend(host),
            new GodotDirectionEffectBackend(host));
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
        Assert(host.PublishedView?.Source is StageViewSource,
            "Default Direction View stopped observing main Stage.");
        var firstActorRoot = GetVisibleActorRoot(host);
        var firstActor = firstActorRoot.GetNode<Sprite2D>("hero");
        Assert(firstActor.Position.IsEqualApprox(new Vector2(160, 360)),
            "Direction Actor did not convert scene percentages into Stage coordinates.");
        Assert(firstActor.Scale.IsEqualApprox(new Vector2(1.25f, 1.25f)) &&
                firstActor.ZIndex == 7 && firstActor.Texture is not null,
            "Direction Actor did not commit appearance, scale, and z-index together.");

        var cgKey = new ResourceKey("tests/headless/fixtures/background_light.svg");
        var cgTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ViewPatchFor(
                    new CgViewSource(cgKey),
                    new ViewPose(new DirectionPoint(37.384f, 74.816f), 3, 10),
                    new DirectionTransitionSpec(
                        DirectionTransitionKind.Dissolve,
                        2,
                        DirectionEase.SineInOut)),
            });

        Assert(host.PublishedView?.Source is CgViewSource cg && cg.Resource == cgKey,
            "CG did not become the main View source at Commit.");
        Assert(Equals(host.PublishedView?.Pose,
                new ViewPose(new DirectionPoint(37.384f, 74.816f), 3, 10)),
            "CG View Pose was not committed atomically.");
        Assert(host.TransitionSource.Visible,
            "CG dissolve did not retain a prepared source frame.");
        Assert(ReferenceEquals(stageBackdropTexture, host.Backdrop.Texture),
            "CG View mutation replaced the Stage Backdrop texture.");
        Assert(host.PublishedBackdrop?.Resource == backdropKey,
            "CG View mutation changed the Stage Backdrop state.");

        await coordinator.FinishAsync(cgTransaction);
        Assert(!host.TransitionSource.Visible && host.TransitionSource.Texture is null,
            "Finishing a CG dissolve leaked its transition capture.");

        await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                ViewPatchFor(
                    new StageViewSource(StageId.Main),
                    ViewPose.Default,
                    DirectionTransitionSpec.Cut),
            },
            DirectionCompletionBarrier.Settle);

        Assert(host.PublishedView?.Source is StageViewSource stage &&
                stage.StageId == StageId.Main,
            "Direction View did not return from CG to main Stage.");
        Assert(ReferenceEquals(stageBackdropTexture, host.Backdrop.Texture),
            "Returning from CG rebuilt or replaced the stable Stage Backdrop.");

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
                    new DirectionTransitionSpec(DirectionTransitionKind.Move, 0.2)),
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
                    new DirectionTransitionSpec(DirectionTransitionKind.Fade, 0.2)),
            });
        var fadedActorRoot = GetLatestVisibleActorRoot(host);
        var fadedActor = fadedActorRoot.GetNode<Sprite2D>("hero");
        Assert(movedActorRoot.Visible && Mathf.IsZeroApprox(fadedActor.Modulate.A),
            "Direction fade did not retain the old Actor visual over a transparent target.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(fadedActor.Modulate.A is > 0 and < 1 &&
                movedActor.Modulate.A is > 0 and < 1,
            "Direction fade did not expose an intermediate per-Actor mix.");
        await coordinator.FinishAsync(fadeTransaction);
        Assert(Mathf.IsEqualApprox(fadedActor.Modulate.A, 1) &&
                (!IsInstanceValid(movedActorRoot) || !movedActorRoot.Visible),
            "Finishing Direction fade did not publish only the target Actor visual.");

        var revisionBeforeFailure = coordinator.State.Revision;
        var backdropBeforeFailure = host.PublishedBackdrop;
        _ = await AssertThrowsAsync<ActorException>(() => coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(cgKey),
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
                ReferenceEquals(GetVisibleActorRoot(host), fadedActorRoot),
            "Failed Actor Prepare partially published Stage state.");

        _ = await AssertThrowsAsync<DirectionException>(() => coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new ActorLeavePatch(
                    StageId.Main,
                    new ActorId("hero"),
                    new DirectionTransitionSpec(DirectionTransitionKind.Move, 0.2)),
            },
            DirectionCompletionBarrier.Settle));
        Assert(ReferenceEquals(GetVisibleActorRoot(host), fadedActorRoot),
            "Rejected move-leave changed the stable Actor tree.");

        var leaveTransaction = await coordinator.ExecuteAsync(
            new IDirectionDirective[]
            {
                new ActorLeavePatch(
                    StageId.Main,
                    new ActorId("hero"),
                    new DirectionTransitionSpec(DirectionTransitionKind.Fade, 0.2)),
            });
        Assert(fadedActorRoot.Visible && GetLatestVisibleActorRoot(host).GetChildCount() == 0,
            "Direction fade-leave did not retain its source Actor during transition.");
        await coordinator.FinishAsync(leaveTransaction);
        Assert(GetVisibleActorRoot(host).GetChildCount() == 0,
            "Direction leave did not publish an empty target actor tree.");

        await stageBackend.ResetAsync(CancellationToken.None);
        await host.ResetAsync(CancellationToken.None);
        Assert(host.PublishedBackdrop is null && host.Backdrop.Texture is null,
            "Direction render host Reset retained its Backdrop.");
        Assert(host.PublishedView?.Source is StageViewSource,
            "Direction render host Reset did not restore main Stage source.");
        Assert(host.Actors.GetChildCount() == 0,
            "Direction render Reset retained an Actor tree.");

        await stageBackend.ShutdownAsync(CancellationToken.None);
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
                !IsInstanceValid(host.OutputLayer),
            "Direction render host leaked viewport or output nodes after Shutdown.");
    }

    private static ViewPatch ViewPatchFor(
        ViewSource source,
        ViewPose pose,
        DirectionTransitionSpec transition) => new(
            ViewId.Main,
            DirectionValue<ViewSource>.From(source),
            DirectionValue<DirectionPoint>.From(pose.Center),
            DirectionValue<float>.From(pose.Zoom),
            DirectionValue<float>.From(pose.RotationDegrees),
            transition);

    private static ActorPatch ActorPatchFor(
        string actorId,
        DirectionPoint position,
        string appearance,
        float scale,
        int z,
        DirectionTransitionSpec? transition = null) => new(
            StageId.Main,
            new ActorId(actorId),
            DirectionValue<DirectionPoint>.From(position),
            DirectionValue<string?>.From(appearance),
            DirectionValue<float>.From(scale),
            DirectionValue<int>.From(z),
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

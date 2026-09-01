using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionRuntimeTests
{
    [Fact]
    public void DefaultStateProvidesMainStageViewAndScreen()
    {
        var state = DirectionState.CreateDefault();

        Assert.Contains(StageId.Main, state.Stages.Keys);
        var view = Assert.IsType<StageViewSource>(state.Views[ViewId.Main].Source);
        Assert.Equal(StageId.Main, view.StageId);
        Assert.Equal(ViewId.Main, state.Screen.Bindings[LayoutSlotId.Main]);
        Assert.Equal(0, state.Revision);
    }

    [Fact]
    public void PlannerBuildsCompleteTargetStateAndResolvesSameBeatDefinitions()
    {
        var planner = new DirectionPlanner();
        var cell = new StageId("cell");
        var reaction = new ViewId("reaction");
        var ema = new ActorId("Ema");

        var plan = planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(reaction, source: new StageViewSource(cell), zoom: 1.8f),
                Actor(ema, cell, position: new DirectionPoint(70, 0)),
                Actor(ema, cell, appearance: "Fearful1,ArmR1"),
                new BackdropPatch(
                    cell,
                    new BackdropState(new ResourceKey("Background_025_001")),
                    DirectionTransitionSpec.Cut),
                new ScreenPatch(
                    new LayoutId("split"),
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [new LayoutSlotId("main")] = ViewId.Main,
                        [new LayoutSlotId("inset")] = reaction,
                    },
                    DirectionTransitionSpec.Cut),
            });

        var stage = plan.TargetState.Stages[cell];
        Assert.Equal("Background_025_001", stage.Backdrop?.Resource.ToString());
        Assert.Equal(new DirectionPoint(70, 0), stage.Actors[ema].Position);
        Assert.Equal("Fearful1,ArmR1", stage.Actors[ema].Appearance);
        Assert.Equal(cell, Assert.IsType<StageViewSource>(
            plan.TargetState.Views[reaction].Source).StageId);
        Assert.Equal(1.8f, plan.TargetState.Views[reaction].Pose.Zoom);
        Assert.Equal(reaction, plan.TargetState.Screen.Bindings[new LayoutSlotId("inset")]);
        Assert.Equal(1, plan.TargetState.Revision);
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.Backdrop &&
            claim.SemanticId == "Background_025_001");
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.ActorAppearance &&
            claim.SemanticId == "Ema/Fearful1,ArmR1");
    }

    [Fact]
    public void PlannerRejectsTwoWritesToTheSameStablePathWithLocations()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Trial", 12, 1);
        var second = new DirectionSourceLocation("Trial", 13, 2);

        var exception = Assert.Throws<DirectionConflictException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f, location: first),
                View(ViewId.Main, zoom: 2, location: second),
            }));

        Assert.Equal("view/main/zoom", exception.StablePath);
        Assert.Equal(first, exception.First);
        Assert.Equal(second, exception.Second);
        Assert.Contains("Trial:12#1", exception.Message);
        Assert.Contains("Trial:13#2", exception.Message);
    }

    [Fact]
    public void PlannerRejectsUnknownStageAndViewReferences()
    {
        var planner = new DirectionPlanner();

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new[]
            {
                View(ViewId.Main, source: new StageViewSource(new StageId("missing"))),
            }));

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new ScreenPatch(
                    LayoutId.Single,
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [LayoutSlotId.Main] = new ViewId("missing"),
                    },
                    DirectionTransitionSpec.Cut),
            }));
    }

    [Fact]
    public void CgChangesViewSourceWithoutMutatingStage()
    {
        var before = DirectionState.CreateDefault();
        var planner = new DirectionPlanner();

        var plan = planner.Plan(before, new[]
        {
            View(
                ViewId.Main,
                source: new CgViewSource(new ResourceKey("Still_490_001")),
                zoom: 3),
        });

        Assert.Same(before.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main]);
        var source = Assert.IsType<CgViewSource>(plan.TargetState.Views[ViewId.Main].Source);
        Assert.Equal("Still_490_001", source.Resource.ToString());
        Assert.Equal(3, plan.TargetState.Views[ViewId.Main].Pose.Zoom);
    }

    [Fact(Timeout = 5_000)]
    public async Task PrepareFailureDoesNotPublishStateOrCommitBackend()
    {
        var backend = new FakeDirectionBackend { PrepareException = new ExpectedException() };
        var coordinator = CreateCoordinator(backend);
        var before = coordinator.State;

        await Assert.ThrowsAsync<ExpectedException>(() => coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }));

        Assert.Same(before, coordinator.State);
        Assert.Equal(0, backend.CommitCount);
        Assert.Null(backend.VisibleState);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommitPublishesOneRevisionAndUsesPlanToken()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);

        var transaction = await coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        Assert.Equal(1, coordinator.State.Revision);
        Assert.Equal(coordinator.State, backend.VisibleState);
        Assert.Equal(transaction.Plan.Id, backend.LastToken?.TransactionId);
        Assert.Equal(1, backend.LastToken?.Revision);
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.Equal(1, backend.CommitCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommitBarrierReturnsBeforeSettlementAndSkipFinishesTransition()
    {
        var backend = new FakeDirectionBackend();
        var coordinator = CreateCoordinator(backend);

        var transaction = await coordinator.ExecuteAsync(new[]
        {
            View(
                ViewId.Main,
                zoom: 2,
                transition: new DirectionTransitionSpec(
                    DirectionTransitionKind.Move,
                    2,
                    Skippable: true)),
        });

        Assert.Equal(DirectionTransactionStatus.Committed, transaction.Status);
        Assert.False(transaction.Settlement.IsCompleted);

        await coordinator.FinishSkippableAsync();

        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.Equal(1, backend.FinishCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task RollbackRestoresBeforeStateAsANewRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var transaction = await coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        var rollback = await coordinator.RollbackAsync(transaction);

        Assert.Null(coordinator.State.Stages[StageId.Main].Backdrop);
        Assert.Equal(2, coordinator.State.Revision);
        Assert.Equal(1, rollback.Plan.BeforeState.Revision);
        Assert.Equal(2, backend.CommitCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task RollbackRejectsStaleTransactionRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var first = await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, zoom: 2),
        }, DirectionCompletionBarrier.Settle);
        await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, rotation: 10),
        }, DirectionCompletionBarrier.Settle);

        var exception = await Assert.ThrowsAsync<DirectionRevisionException>(() =>
            coordinator.RollbackAsync(first));

        Assert.Equal(1, exception.Expected);
        Assert.Equal(2, exception.Actual);
    }

    [Fact(Timeout = 5_000)]
    public async Task BeatCollectsExplicitlyAndCommitsOneRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("opening", "Adv02");

        var first = await beats.SubmitAsync(new BackdropPatch(
            StageId.Main,
            new BackdropState(new ResourceKey("Background_001_001")),
            DirectionTransitionSpec.Cut));
        var second = await beats.SubmitAsync(View(ViewId.Main, zoom: 2));

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(0, coordinator.State.Revision);
        Assert.Equal(0, backend.CommitCount);

        await beats.EndAsync();

        Assert.Equal(1, coordinator.State.Revision);
        Assert.Equal(1, backend.CommitCount);
        Assert.Equal(2, coordinator.State.Views[ViewId.Main].Pose.Zoom);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdjacentDirectivesOutsideBeatRemainSeparateTransactions()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);

        await beats.SubmitAsync(new BackdropPatch(
            StageId.Main,
            new BackdropState(new ResourceKey("Background_001_001")),
            DirectionTransitionSpec.Cut), DirectionCompletionBarrier.Settle);
        await beats.SubmitAsync(
            View(ViewId.Main, zoom: 2),
            DirectionCompletionBarrier.Settle);

        Assert.Equal(2, coordinator.State.Revision);
        Assert.Equal(2, backend.CommitCount);
    }

    [Fact]
    public async Task OpenBeatCanAbortButCannotNestOrCrossNodeBoundary()
    {
        var coordinator = CreateCoordinator(new FakeDirectionBackend { AutoSettle = true });
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("opening", "Adv02");
        await beats.SubmitAsync(View(ViewId.Main, zoom: 2));

        Assert.Throws<DirectionBeatException>(() => beats.Begin("nested", "Adv02"));
        Assert.Throws<DirectionBeatException>(() => beats.ValidateNodeBoundary("Adv02"));

        beats.AbortOpen();

        Assert.False(beats.HasOpenBeat);
        Assert.Equal(0, coordinator.State.Revision);
    }

    [Fact(Timeout = 5_000)]
    public async Task NamedBeatCanJoinAndFinishAfterCommitBarrier()
    {
        var backend = new FakeDirectionBackend();
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("slow-pan", "Trial");
        await beats.SubmitAsync(View(
            ViewId.Main,
            zoom: 2,
            transition: new DirectionTransitionSpec(DirectionTransitionKind.Move, 4)));
        await beats.EndAsync(DirectionCompletionBarrier.Commit);

        var join = beats.JoinAsync("slow-pan");
        Assert.False(join.IsCompleted);

        await beats.FinishAsync("slow-pan");
        await join;

        Assert.Equal(1, backend.FinishCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeBackendPreparesEveryChangedComponentBeforeCommit()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components,
            components,
            components,
            components);
        var planner = new DirectionPlanner();
        var plan = planner.Plan(DirectionState.CreateDefault(), new IDirectionDirective[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
            View(ViewId.Main, zoom: 2),
        });

        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);

        Assert.Equal(new[] { "stage:main", "view:main" }, components.PrepareCalls);
        Assert.Empty(components.CommitTokens);

        var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
        prepared.Commit(token);

        Assert.Equal(2, components.CommitTokens.Count);
        Assert.All(components.CommitTokens, actual => Assert.Equal(token, actual));
        Assert.False(prepared.Settlement.IsCompleted);

        await prepared.FinishAsync(CancellationToken.None);
        await prepared.Settlement;
        await prepared.DisposeAsync();

        Assert.Equal(2, components.FinishCount);
        Assert.Equal(2, components.DisposeCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeBackendDisposesSuccessfulPreparationsWhenAnotherFails()
    {
        var components = new ComponentBackendHarness { FailViewPreparation = true };
        var backend = new CompositeDirectionPreparationBackend(
            components,
            components,
            components,
            components);
        var plan = new DirectionPlanner().Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(new ResourceKey("Background_001_001")),
                    DirectionTransitionSpec.Cut),
                View(ViewId.Main, zoom: 2),
            });

        await Assert.ThrowsAsync<ExpectedException>(() =>
            backend.PrepareAsync(plan, CancellationToken.None));

        Assert.Equal(1, components.DisposeCount);
        Assert.Empty(components.CommitTokens);
    }

    private static DirectionTransactionCoordinator CreateCoordinator(
        IDirectionPreparationBackend backend) =>
        new(new DirectionPlanner(), backend);

    private static ActorPatch Actor(
        ActorId actorId,
        StageId stageId,
        DirectionPoint? position = null,
        string? appearance = null) => new(
            stageId,
            actorId,
            position is { } point
                ? DirectionValue<DirectionPoint>.From(point)
                : default,
            appearance is not null
                ? DirectionValue<string?>.From(appearance)
                : default,
            default,
            default,
            DirectionTransitionSpec.Cut);

    private static ViewPatch View(
        ViewId viewId,
        ViewSource? source = null,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            source is not null
                ? DirectionValue<ViewSource>.From(source)
                : default,
            default,
            zoom is { } zoomValue
                ? DirectionValue<float>.From(zoomValue)
                : default,
            rotation is { } rotationValue
                ? DirectionValue<float>.From(rotationValue)
                : default,
            transition ?? DirectionTransitionSpec.Cut,
            location);

    private sealed class FakeDirectionBackend : IDirectionPreparationBackend
    {
        public Exception? PrepareException { get; init; }
        public bool AutoSettle { get; init; }
        public int CommitCount { get; private set; }
        public int FinishCount { get; private set; }
        public DirectionState? VisibleState { get; private set; }
        public DirectionCommitToken? LastToken { get; private set; }

        public Task<IPreparedDirectionTransaction> PrepareAsync(
            DirectionPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareException is not null) throw PrepareException;
            return Task.FromResult<IPreparedDirectionTransaction>(new Prepared(this, plan));
        }

        private sealed class Prepared : IPreparedDirectionTransaction
        {
            private readonly FakeDirectionBackend owner;
            private readonly DirectionPlan plan;
            private readonly TaskCompletionSource settlement = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private bool committed;

            public Prepared(FakeDirectionBackend owner, DirectionPlan plan)
            {
                this.owner = owner;
                this.plan = plan;
            }

            public Task Settlement => settlement.Task;

            public void Commit(DirectionCommitToken token)
            {
                if (committed) throw new InvalidOperationException("Already committed.");
                committed = true;
                owner.CommitCount++;
                owner.LastToken = token;
                owner.VisibleState = plan.TargetState;
                if (owner.AutoSettle) settlement.TrySetResult();
            }

            public ValueTask FinishAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!committed) throw new InvalidOperationException("Not committed.");
                owner.FinishCount++;
                settlement.TrySetResult();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!committed) settlement.TrySetCanceled();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ComponentBackendHarness :
        IDirectionStageBackend,
        IDirectionViewBackend,
        IDirectionScreenBackend,
        IDirectionEffectBackend
    {
        public bool FailViewPreparation { get; init; }
        public List<string> PrepareCalls { get; } = new();
        public List<DirectionCommitToken> CommitTokens { get; } = new();
        public int FinishCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<IPreparedDirectionComponent> PrepareAsync(
            StageId stageId,
            StageState? before,
            StageState? target,
            CancellationToken cancellationToken) =>
            Prepare($"stage:{stageId}", fail: false, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            ViewId viewId,
            ViewState? before,
            ViewState? target,
            CancellationToken cancellationToken) =>
            Prepare($"view:{viewId}", FailViewPreparation, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            ScreenState before,
            ScreenState target,
            CancellationToken cancellationToken) =>
            Prepare("screen", fail: false, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            StableEffectState before,
            StableEffectState target,
            CancellationToken cancellationToken) =>
            Prepare("effects", fail: false, cancellationToken);

        private Task<IPreparedDirectionComponent> Prepare(
            string name,
            bool fail,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls.Add(name);
            if (fail) return Task.FromException<IPreparedDirectionComponent>(new ExpectedException());
            return Task.FromResult<IPreparedDirectionComponent>(new Component(this));
        }

        private sealed class Component : IPreparedDirectionComponent
        {
            private readonly ComponentBackendHarness owner;
            private readonly TaskCompletionSource settlement = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Component(ComponentBackendHarness owner) => this.owner = owner;

            public Task Settlement => settlement.Task;

            public void Commit(DirectionCommitToken token) => owner.CommitTokens.Add(token);

            public ValueTask FinishAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.FinishCount++;
                settlement.TrySetResult();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ExpectedException : Exception;
}

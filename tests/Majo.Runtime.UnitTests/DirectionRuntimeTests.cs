#nullable enable

using System.Text.Json;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionRuntimeTests
{
    [Fact]
    public void DirectionCommandModuleRegistersOnlyTheFinalDslSurface()
    {
        var services = new ServiceResolverBuilder()
            .Add<IDirectionTransactionCoordinator>(new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true }))
            .Add<IDirectionBeatRuntime>(new DirectionBeatRuntime(CreateCoordinator(
                new FakeDirectionBackend { AutoSettle = true })))
            .Add<IDirectionScopeResolver>(new DirectionScopeResolver())
            .Add<IDirectionCommandPolicy>(new DirectionCommandPolicy())
            .Add<IDialogueNodeAccessor>(new DelegateDialogueNodeAccessor(() => "Opening"))
            .Build();

        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new DirectionCommandModule());
        commands.SealRegistrations();

        var names = commands.BuildCatalog(services).Commands
            .Select(command => command.Name)
            .ToArray();

        Assert.Equal(
            [
                "stage define",
                "stage clear",
                "backdrop",
                "character",
                "character hide",
                "object",
                "character move",
                "object move",
                "object hide",
                "character fx",
                "object fx",
                "backdrop fx",
                "view define",
                "camera preset define",
                "camera",
                "camera move",
                "camera reset",
                "camera fx",
                "shot use",
                "screen show",
                "screen split",
                "screen pip",
                "import",
                "mark",
                "curtain",
                "beat begin",
                "beat end",
                "beat join",
                "beat finish",
                "beat abort",
            ],
            names);
        Assert.DoesNotContain(names, name => name is
            "layout" or "stage show" or "back" or "char" or "hide" or "place" or
            "shot pose" or "shake" or "camera zoom" or "camera rotate");
    }

    [Fact]
    public async Task DirectionStateContributorRestoresThroughAtomicCoordinatorCommit()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var contributor = new DirectionStateContributor(coordinator);
        var initial = await contributor.CaptureAsync(default);

        await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new StageCreatePatch(
                new StageId("broadcast"),
                new ViewId("broadcast"),
                DirectionTransitionSpec.Cut),
            new BackdropPatch(
                new StageId("broadcast"),
                new BackdropState(new ResourceKey("Background_025_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);
        Assert.Contains(new StageId("broadcast"), coordinator.State.Stages.Keys);

        await contributor.PreflightAsync(initial, default);
        await contributor.CommitPreparedAsync(initial, default);

        Assert.DoesNotContain(new StageId("broadcast"), coordinator.State.Stages.Keys);
        Assert.True(coordinator.State.Revision > 0);
    }

    [Fact]
    public async Task DirectionStateContributorPersistsStagesViewsAndScreenBindings()
    {
        var coordinator = CreateCoordinator(new FakeDirectionBackend { AutoSettle = true });
        var contributor = new DirectionStateContributor(coordinator);
        var illustration = new StageId("illustration");
        var illustrationView = new ViewId("illustration");
        var illustrationPose = new ViewPose(new DirectionPoint(44, 72), 3, 10);
        await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new StageCreatePatch(illustration, illustrationView, DirectionTransitionSpec.Cut),
            new BackdropPatch(
                illustration,
                new BackdropState(new ResourceKey("Still_490_001")),
                DirectionTransitionSpec.Cut),
            View(
                illustrationView,
                center: illustrationPose.Center,
                zoom: illustrationPose.Zoom,
                rotation: illustrationPose.RotationDegrees),
            new ScreenPatch(
                LayoutId.SingleLayout,
                new Dictionary<LayoutSlotId, ViewId>
                {
                    [LayoutSlotId.Main] = illustrationView,
                },
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);
        var snapshot = await contributor.CaptureAsync(default);

        await coordinator.ResetAsync(default);
        await contributor.CommitPreparedAsync(snapshot, default);
        Assert.Equal(
            new ResourceKey("Still_490_001"),
            coordinator.State.Stages[illustration].Backdrop?.Resource);
        Assert.Equal(illustration, coordinator.State.Views[illustrationView].StageId);
        Assert.Equal(illustrationPose, coordinator.State.Views[illustrationView].Pose);
        Assert.Equal(illustrationView, coordinator.State.Screen.Bindings[LayoutSlotId.Main]);
    }

    [Fact]
    public async Task DirectionStateContributorRejectsSnapshotsMissingMainStageOrView()
    {
        var contributor = new DirectionStateContributor(
            CreateCoordinator(new FakeDirectionBackend { AutoSettle = true }));
        const string mainStage = "[{\"id\":\"main\",\"effects\":{\"tint\":{\"r\":1,\"g\":1,\"b\":1,\"a\":1}},\"actors\":[]}]";
        const string mainView = "[{\"id\":\"main\",\"stageId\":\"main\",\"centerX\":50,\"centerY\":50,\"zoom\":1,\"rotation\":0}]";
        var missingStage = CreateDirectionRecord("[]", mainView);
        var missingView = CreateDirectionRecord(mainStage, "[]");

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            contributor.PreflightAsync(missingStage, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            contributor.PreflightAsync(missingView, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DirectionStateContributorRejectsVersionOneSnapshots()
    {
        var contributor = new DirectionStateContributor(
            CreateCoordinator(new FakeDirectionBackend { AutoSettle = true }));
        var record = new StateRecord(
            BuiltInStateKeys.Direction,
            StateScope.Game,
            1,
            JsonDocument.Parse("""
                {
                  "stages":[{"id":"main","backdrop":null,"actors":[]}],
                  "views":[{"id":"main","source":"stage:main","centerX":50,"centerY":50,"zoom":1,"rotation":0}],
                  "layout":"single",
                  "bindings":{"main":"main"},
                  "revision":0
                }
                """).RootElement.Clone());

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            contributor.PreflightAsync(record, CancellationToken.None).AsTask());
    }

    [Fact(Timeout = 5_000)]
    public async Task PrepareFailureDoesNotPublishStateOrCommitBackend()
    {
        var backend = new FakeDirectionBackend { PrepareException = new ExpectedDirectionException() };
        var coordinator = CreateCoordinator(backend);
        var before = coordinator.State;

        await Assert.ThrowsAsync<ExpectedDirectionException>(() => coordinator.ExecuteAsync(new[]
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
                    DirectionTransitionId.Move,
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

    [Fact]
    public void CurtainPatchIsDurableAndUsesEffectTransition()
    {
        var planner = new DirectionPlanner();
        var plan = planner.Plan(DirectionState.CreateDefault(), new IDirectionDirective[]
        {
            new CurtainPatch(
                new CurtainState(new DirectionColor(0.1f, 0.2f, 0.3f), 1),
                new DirectionTransitionSpec(DirectionTransitionId.Fade, 0.5)),
        });

        Assert.Equal(1, plan.TargetState.Effects.Curtain.Opacity);
        var transition = Assert.Single(plan.Transitions);
        Assert.Contains("effect/curtain", transition.StablePaths);
        Assert.Equal(DirectionTransitionId.Fade, transition.Spec.Kind);
        Assert.Empty(plan.Cues);
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
    public async Task TransientCueIsExecutedWithTheCommittedTransactionToken()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var cueExecutor = new RecordingCueExecutor();
        var coordinator = CreateCoordinator(backend, cueExecutor);
        var beats = new DirectionBeatRuntime(coordinator, cueExecutor);
        beats.Begin("cue-beat", "Opening");
        await beats.SubmitAsync(new DirectionCue(
            "test", new Dictionary<string, string>()));

        var transaction = await beats.EndAsync(DirectionCompletionBarrier.Commit);

        Assert.Equal(transaction.Token, cueExecutor.Token);
        Assert.Single(cueExecutor.Cues);
        Assert.Equal(transaction.Plan.Id, cueExecutor.Token?.TransactionId);
        Assert.Equal(1, cueExecutor.Token?.Revision);
    }

    [Fact]
    public void PlannerReportsBothLocationsForConflictingComponentTransitions()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Opening", 4, 0);
        var second = new DirectionSourceLocation("Opening", 5, 0);

        var exception = Assert.Throws<DirectionConflictException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f,
                    transition: new DirectionTransitionSpec(DirectionTransitionId.Move, 1),
                    location: first),
                View(ViewId.Main, rotation: 10,
                    transition: new DirectionTransitionSpec(DirectionTransitionId.Fade, 1),
                    location: second),
            }));

        Assert.Contains("Opening:4#0", exception.Message);
        Assert.Contains("Opening:5#0", exception.Message);
    }

    [Fact]
    public void PlannerRejectsCutAndMoveInOnePoseSlot()
    {
        var error = Assert.Throws<DirectionConflictException>(() => new DirectionPlanner().Plan(
            DirectionState.CreateDefault(), new IDirectionDirective[]
            {
                View(ViewId.Main, center: new DirectionPoint(30, 50)),
                View(ViewId.Main, zoom: 2, transition: new DirectionTransitionSpec(DirectionTransitionId.Move, .4)),
            }));
        Assert.Equal("view/main/pose", error.StablePath);
    }

    [Fact]
    public void StageReleaseRemovesItsDefaultAndAdditionalViews()
    {
        var planner = new DirectionPlanner();
        var illustration = new StageId("illustration");
        var illustrationView = new ViewId("illustration");
        var closeup = new ViewId("closeup");
        var created = planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new StageCreatePatch(
                    illustration,
                    illustrationView,
                    DirectionTransitionSpec.Cut),
                new ViewCreatePatch(
                    closeup,
                    illustration,
                    DirectionTransitionSpec.Cut),
            }).TargetState;

        var released = planner.Plan(
            created,
            new IDirectionDirective[]
            {
                new StageReleasePatch(illustration, DirectionTransitionSpec.Cut),
            }).TargetState;

        Assert.DoesNotContain(illustration, released.Stages.Keys);
        Assert.DoesNotContain(illustrationView, released.Views.Keys);
        Assert.DoesNotContain(closeup, released.Views.Keys);
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
            transition: new DirectionTransitionSpec(DirectionTransitionId.Move, 4)));
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

        await Assert.ThrowsAsync<ExpectedDirectionException>(() =>
            backend.PrepareAsync(plan, CancellationToken.None));

        Assert.Equal(1, components.DisposeCount);
        Assert.Empty(components.CommitTokens);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeCommitFailureCompensatesAlreadyCommittedComponents()
    {
        var components = new ComponentBackendHarness { FailViewCommit = true };
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

        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);

        Assert.Throws<ExpectedDirectionException>(() => prepared.Commit(
            new DirectionCommitToken(plan.Id, plan.TargetState.Revision)));
        // Rollback is attempted for the component whose Commit threw as well:
        // a backend may have published partial state before reporting failure.
        Assert.Equal(2, components.RollbackCount);
        await prepared.DisposeAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeRollbackIsTerminalAndDisposesEveryComponent()
    {
        var components = new ComponentBackendHarness { ThrowOnDispose = true };
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
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
        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);
        var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);

        prepared.Commit(token);
        prepared.Rollback(token);
        Assert.Throws<InvalidOperationException>(() => prepared.Commit(token));
        await Assert.ThrowsAsync<AggregateException>(() => prepared.DisposeAsync().AsTask());
        Assert.Equal(2, components.DisposeCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task FaultedSettlementIsNotSilentlyAcceptedByFinish()
    {
        var backend = new FakeDirectionBackend { SettlementException = new ExpectedDirectionException() };
        var coordinator = CreateCoordinator(backend);
        var transaction = await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, zoom: 2),
        });

        await Assert.ThrowsAsync<ExpectedDirectionException>(() => transaction.Settlement);
        await Assert.ThrowsAsync<ExpectedDirectionException>(() => coordinator.FinishAsync(transaction));
    }

    [Theory(Timeout = 5_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionAsyncDisposalIsIdempotentAcrossSettlement(bool settleFirst)
    {
        var backend = new FakeDirectionBackend();
        using var coordinator = CreateCoordinator(backend);
        await using var transaction = await coordinator.ExecuteAsync(new[] { View(ViewId.Main, zoom: 2) });
        // Synchronous using must not be offered for a resource with asynchronous cleanup.
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(CommittedDirectionTransaction)));
        if (settleFirst)
        {
            backend.LastSettlementTcs!.SetResult();
            await transaction.Settlement;
        }

        await transaction.DisposeAsync();
        if (!settleFirst)
        {
            backend.LastSettlementTcs!.SetResult();
            await transaction.Settlement;
        }
        await coordinator.FinishAsync(transaction);
        await coordinator.ResetAsync();
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.Equal(1, backend.DisposeCount);
    }

    private static DirectionTransactionCoordinator CreateCoordinator(
        IDirectionPreparationBackend backend,
        ITransientDirectionCueExecutor? cueExecutor = null) =>
        new(new DirectionPlanner(), backend, cues: cueExecutor);

    private static StateRecord CreateDirectionRecord(string stages, string views)
    {
        var json = "{\"stages\":" + stages +
            ",\"views\":" + views +
            ",\"layout\":\"single\",\"bindings\":{\"main\":\"main\"}," +
            "\"effects\":{\"curtain\":{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":1},\"opacity\":0}}," +
            "\"revision\":0}";
        return new StateRecord(
            BuiltInStateKeys.Direction,
            StateScope.Game,
            2,
            JsonDocument.Parse(json).RootElement.Clone());
    }

    private static ViewPosePatch View(
        ViewId viewId,
        DirectionPoint? center = null,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            center is { } centerValue
                ? DirectionValue.From(centerValue)
                : default,
            zoom is { } zoomValue
                ? DirectionValue.From(zoomValue)
                : default,
            rotation is { } rotationValue
                ? DirectionValue.From(rotationValue)
                : default,
            transition ?? DirectionTransitionSpec.Cut,
            location);
}

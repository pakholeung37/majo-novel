#nullable enable

using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CompositeDirectionTransactionTests
{
    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionSettlement_CompletesNaturallyWhenAllComponentsSettle()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var planDirectives = new IDirectionDirective[]
        {
            new StageCreatePatch(StageId.Main, ViewId.Main, DirectionTransitionSpec.Cut),
            new BackdropPatch(StageId.Main, new BackdropState("bg1", "background", "Default"), new DirectionTransitionSpec(DirectionTransitionId.Fade, 1.0)),
        };

        var transaction = await coordinator.ExecuteAsync(planDirectives, DirectionCompletionBarrier.Commit);
        Assert.Equal(DirectionTransactionStatus.Committed, transaction.Status);
        Assert.False(transaction.Settlement.IsCompleted);

        // Components settle
        await coordinator.FinishAsync(transaction);
        await transaction.Settlement;

        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionFinish_ForcesFinishOnAllComponents()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var transaction = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState("bg1", "background", "Default"), new DirectionTransitionSpec(
                DirectionTransitionId.Fade, 1.0, EasingId.Linear)),
        }, DirectionCompletionBarrier.Commit);

        Assert.False(transaction.Settlement.IsCompleted);
        await coordinator.FinishAsync(transaction);

        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
        Assert.True(components.FinishCount > 0);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionRollback_RestoresPreviousStateAndRollsBack()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var initialRevision = coordinator.State.Revision;
        var transaction = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState("bg1", "background", "Default"), DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        Assert.True(coordinator.State.Revision > initialRevision);
        var rolledBack = await coordinator.RollbackAsync(transaction);

        Assert.NotNull(rolledBack);
        Assert.True(coordinator.State.Revision > transaction.Plan.TargetState.Revision);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionFinishSkippable_SkipsOnlySkippableTransactions()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var skippableTx = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState("bg1", "background", "Default"), new DirectionTransitionSpec(
                DirectionTransitionId.Fade, 1.0, EasingId.Linear, Skippable: true)),
        }, DirectionCompletionBarrier.Commit);

        Assert.True(skippableTx.IsSkippable);
        Assert.Equal(DirectionTransactionStatus.Committed, skippableTx.Status);

        await coordinator.FinishSkippableAsync();
        Assert.True(skippableTx.Settlement.IsCompletedSuccessfully);
    }
}

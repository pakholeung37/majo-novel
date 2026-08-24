using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StateEligibilityTests
{
    [Fact]
    public async Task ChecksAreReadOnlyAndAggregateStableRejectionReasons()
    {
        var script = new EligibilityScript
        {
            PhaseValue = ScriptPlaybackPhase.Executing,
        };
        var operations = new EligibilityOperations
        {
            Active = [new EligibilityOperation()],
        };
        var input = new EligibilityInput
        {
            Handles =
            [
                new InputHandleSnapshot(
                    InputHandleKind.Scope,
                    "save-menu",
                    true,
                    "SaveMenu",
                    []),
            ],
        };
        var eligibility = new StateEligibility(script, operations, input);
        await eligibility.InitializeAsync(CancellationToken.None);
        using var barrier = eligibility.AcquireBarrier(
            new object(),
            "combat-resolution",
            diagnostic: "Combat results have not committed.",
            playerMessage: "Finish the current action first.");

        var first = eligibility.Check(StateOperationKind.Save);
        var second = eligibility.Check(StateOperationKind.Save);

        Assert.False(first.IsAllowed);
        Assert.Equal(
            [
                StateEligibilityReasonCode.ScriptUnsafePhase,
                StateEligibilityReasonCode.ActiveOperation,
                StateEligibilityReasonCode.ModalInput,
                StateEligibilityReasonCode.ExternalBarrier,
            ],
            first.Reasons.Select(value => value.Code));
        Assert.Equal(first.Operation, second.Operation);
        Assert.Equal(first.Reasons, second.Reasons);
        Assert.Equal(ScriptPlaybackPhase.Executing, script.Phase);
        Assert.Single(operations.ActiveOperations);
        Assert.Single(input.ActiveHandles);

        barrier.Dispose();
        script.PhaseValue = ScriptPlaybackPhase.AwaitingAdvance;
        operations.Active = [];
        input.Handles = [];
        Assert.True(eligibility.Check(StateOperationKind.Save).IsAllowed);
    }

    [Fact]
    public async Task BarrierCanTargetOnlyOneOperationKind()
    {
        var script = new EligibilityScript
        {
            PhaseValue = ScriptPlaybackPhase.AwaitingAdvance,
        };
        var eligibility = new StateEligibility(
            script,
            new EligibilityOperations(),
            new EligibilityInput());
        await eligibility.InitializeAsync(CancellationToken.None);
        using var barrier = eligibility.AcquireBarrier(
            new object(),
            "no-rollback",
            StateOperationMask.Rollback);

        Assert.True(eligibility.Check(StateOperationKind.Save).IsAllowed);
        Assert.True(eligibility.Check(StateOperationKind.Restore).IsAllowed);
        Assert.False(eligibility.Check(StateOperationKind.Rollback).IsAllowed);
    }

    private sealed class EligibilityScript : IScriptPlayback
    {
        public event Action<ScriptPlaybackPhaseChanged>? PhaseChanged { add { } remove { } }
        public ScriptPlaybackPhase PhaseValue { get; set; }
        public ScriptPlaybackPhase Phase => PhaseValue;
        public ScriptPlaybackSpot? CurrentSpot => null;
        public bool CanCaptureCheckpoint => Phase == ScriptPlaybackPhase.AwaitingAdvance;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ScriptCheckpoint CaptureCheckpoint() => throw new NotSupportedException();
        public Task ValidateCheckpointAsync(
            ScriptCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreCheckpointAsync(
            ScriptCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EligibilityOperations : IOperationScheduler
    {
        public IReadOnlyCollection<IOperationHandle> Active { get; set; } = [];
        public IReadOnlyCollection<IOperationHandle> ActiveOperations => Active;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IOperationHandle Start(OperationDefinition definition) => throw new NotSupportedException();
        public Task WaitAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EligibilityOperation : IOperationHandle
    {
        public string Id => "active";
        public string? GroupId => "test";
        public OperationState State => OperationState.Running;
        public Task Completion => Task.CompletedTask;
        public void Complete() { }
        public void Cancel() { }
    }

    private sealed class EligibilityInput : IInputManager
    {
        public event Action<InputActivation>? Dispatched { add { } remove { } }
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<InputHandleSnapshot> Handles { get; set; } = [];
        public int ActiveHandleCount => Handles.Count;
        public IReadOnlyList<InputHandleSnapshot> ActiveHandles => Handles;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IInputHandle AcquireScope(object owner, InputScopeOptions? options = null) =>
            throw new NotSupportedException();
        public IInputHandle AcquireMuter(object owner, InputMuteOptions? options = null) =>
            throw new NotSupportedException();
        public void ReleaseOwner(object owner) { }
    }
}

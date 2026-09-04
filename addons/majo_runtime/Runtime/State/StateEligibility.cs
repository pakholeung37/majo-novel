#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

public enum StateOperationKind
{
    Save,
    Restore,
    Rollback,
}

[Flags]
public enum StateOperationMask
{
    None = 0,
    Save = 1,
    Restore = 2,
    Rollback = 4,
    All = Save | Restore | Rollback,
}

public enum StateEligibilityReasonCode
{
    ScriptUnsafePhase,
    StateTransactionActive,
    ActiveCommand,
    ActiveOperation,
    ModalInput,
    ExternalBarrier,
    UnsupportedRollbackContributor,
}

public sealed record StateEligibilityReason(
    StateEligibilityReasonCode Code,
    string Diagnostic,
    string? PlayerMessage = null);

public sealed record StateEligibilityResult(
    StateOperationKind Operation,
    IReadOnlyList<StateEligibilityReason> Reasons)
{
    public bool IsAllowed => Reasons.Count == 0;
}

public interface IStateBarrierHandle : IDisposable
{
    string ReasonCode { get; }
    StateOperationMask Operations { get; }
    bool IsDisposed { get; }
}

public interface IStateEligibility : IRuntimeService
{
    StateEligibilityResult Check(StateOperationKind operation);

    IStateBarrierHandle AcquireBarrier(
        object owner,
        string reasonCode,
        StateOperationMask operations = StateOperationMask.All,
        string? diagnostic = null,
        string? playerMessage = null);
}

public sealed class StateEligibility : IStateEligibility
{
    private readonly object sync = new();
    private readonly IScriptPlayback script;
    private readonly IRuntimeActivityObserver activities;
    private readonly IInputManager input;
    private readonly IYarnCommandAdapter? commands;
    private readonly List<BarrierHandle> barriers = new();
    private Func<StateManagerStatus>? statusProvider;
    private IReadOnlyList<IStateContributor> contributors = Array.Empty<IStateContributor>();
    private bool isInitialized;

    public StateEligibility(
        IScriptPlayback script,
        IRuntimeActivityObserver activities,
        IInputManager input,
        IYarnCommandAdapter? commands = null)
    {
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        this.activities = activities ?? throw new ArgumentNullException(nameof(activities));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.commands = commands;
    }

    public StateEligibilityResult? LastResult { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearBarriers();
        LastResult = null;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearBarriers();
        LastResult = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public StateEligibilityResult Check(StateOperationKind operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        var reasons = new List<StateEligibilityReason>();
        var isDestructiveRestore = operation is StateOperationKind.Restore or StateOperationKind.Rollback;
        var safeScriptPhase = operation == StateOperationKind.Save
            ? script.Phase == ScriptPlaybackPhase.AwaitingAdvance
            : script.Phase is ScriptPlaybackPhase.AwaitingAdvance or ScriptPlaybackPhase.Idle;
        if (!isDestructiveRestore && !safeScriptPhase)
        {
            reasons.Add(new StateEligibilityReason(
                StateEligibilityReasonCode.ScriptUnsafePhase,
                $"Script phase '{script.Phase}' is not a durable state boundary."));
        }

        var status = statusProvider?.Invoke() ?? StateManagerStatus.Idle;
        if (status != StateManagerStatus.Idle)
        {
            reasons.Add(new StateEligibilityReason(
                StateEligibilityReasonCode.StateTransactionActive,
                $"State manager is '{status}'."));
        }

        if (!isDestructiveRestore && commands?.HasActiveCommands == true)
        {
            reasons.Add(new StateEligibilityReason(
                StateEligibilityReasonCode.ActiveCommand,
                "A runtime command is active."));
        }

        if (!isDestructiveRestore && activities.HasActiveActivities(includeIgnorable: false))
        {
            var activeCount = activities.Snapshot().Count(a => !a.IsIgnorable);
            reasons.Add(new StateEligibilityReason(
                StateEligibilityReasonCode.ActiveOperation,
                $"{activeCount} operation(s) are active."));
        }

        var modal = input.ActiveHandles.FirstOrDefault(value => value.IsModal);
        if (!isDestructiveRestore && modal is not null)
        {
            reasons.Add(new StateEligibilityReason(
                StateEligibilityReasonCode.ModalInput,
                $"Modal input owner '{modal.OwnerName}' ({modal.Name ?? "unnamed"}) is active."));
        }

        var requestedMask = ToMask(operation);
        lock (sync)
        {
            foreach (var barrier in barriers.Where(value =>
                         !value.IsDisposed && (value.Operations & requestedMask) != 0))
            {
                reasons.Add(new StateEligibilityReason(
                    StateEligibilityReasonCode.ExternalBarrier,
                    $"External barrier '{barrier.ReasonCode}': {barrier.Diagnostic}",
                    barrier.PlayerMessage));
            }
        }

        if (operation == StateOperationKind.Rollback)
        {
            foreach (var contributor in contributors.Where(value =>
                         value.Capability.RollbackPolicy == StateRollbackPolicy.Unsupported &&
                         value is IStateContributorActivity { IsActive: true }))
            {
                reasons.Add(new StateEligibilityReason(
                    StateEligibilityReasonCode.UnsupportedRollbackContributor,
                    $"Contributor '{contributor.Key}' is active and does not support rollback."));
            }
        }

        LastResult = new StateEligibilityResult(operation, reasons.AsReadOnly());
        return LastResult;
    }

    public IStateBarrierHandle AcquireBarrier(
        object owner,
        string reasonCode,
        StateOperationMask operations = StateOperationMask.All,
        string? diagnostic = null,
        string? playerMessage = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (!isInitialized)
        {
            throw new StateRuntimeException("State eligibility is not initialized.");
        }

        if (operations == StateOperationMask.None ||
            (operations & ~StateOperationMask.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operations));
        }

        var handle = new BarrierHandle(
            this,
            owner,
            reasonCode.Trim(),
            operations,
            string.IsNullOrWhiteSpace(diagnostic) ? "No diagnostic supplied." : diagnostic.Trim(),
            string.IsNullOrWhiteSpace(playerMessage) ? null : playerMessage.Trim());
        lock (sync)
        {
            barriers.Add(handle);
        }
        return handle;
    }

    internal void Attach(
        Func<StateManagerStatus> transactionStatusProvider,
        IReadOnlyList<IStateContributor> stateContributors)
    {
        statusProvider = transactionStatusProvider ??
            throw new ArgumentNullException(nameof(transactionStatusProvider));
        contributors = stateContributors ?? throw new ArgumentNullException(nameof(stateContributors));
    }

    private void Release(BarrierHandle handle)
    {
        lock (sync)
        {
            barriers.Remove(handle);
        }
    }

    private void ClearBarriers()
    {
        BarrierHandle[] snapshot;
        lock (sync)
        {
            snapshot = barriers.ToArray();
            barriers.Clear();
        }
        foreach (var barrier in snapshot)
        {
            barrier.DisposeFromOwner();
        }
    }

    private static StateOperationMask ToMask(StateOperationKind operation) => operation switch
    {
        StateOperationKind.Save => StateOperationMask.Save,
        StateOperationKind.Restore => StateOperationMask.Restore,
        StateOperationKind.Rollback => StateOperationMask.Rollback,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private sealed class BarrierHandle : IStateBarrierHandle
    {
        private StateEligibility? ownerService;

        public BarrierHandle(
            StateEligibility ownerService,
            object owner,
            string reasonCode,
            StateOperationMask operations,
            string diagnostic,
            string? playerMessage)
        {
            this.ownerService = ownerService;
            Owner = owner;
            ReasonCode = reasonCode;
            Operations = operations;
            Diagnostic = diagnostic;
            PlayerMessage = playerMessage;
        }

        public object Owner { get; }
        public string ReasonCode { get; }
        public StateOperationMask Operations { get; }
        public string Diagnostic { get; }
        public string? PlayerMessage { get; }
        public bool IsDisposed => ownerService is null;

        public void Dispose()
        {
            var service = Interlocked.Exchange(ref ownerService, null);
            service?.Release(this);
        }

        public void DisposeFromOwner() => Interlocked.Exchange(ref ownerService, null);
    }
}

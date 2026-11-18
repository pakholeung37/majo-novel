#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.History;
using Majo.Runtime.State;
using static Majo.Runtime.Save.SaveSlotMetadataCodec;

namespace Majo.Runtime.Save;

internal sealed class SaveSlotOperationTracker(IRuntimeLogger logger)
{
    public event Action<SaveSlotOperationInfo>? OperationStarted;
    public event Action<SaveSlotOperationInfo>? OperationProgressed;
    public event Action<SaveSlotOperationInfo>? OperationCompleted;
    public event Action<SaveSlotOperationInfo>? OperationFailed;
    public event Action? SlotsChanged;
    public SaveSlotManagerStatus Status { get; private set; } = SaveSlotManagerStatus.Idle;
    public SaveSlotOperationInfo? CurrentOperation { get; private set; }
    public string? LastFailureMessage { get; private set; }
    public SaveSlotOperationPhase? LastFailurePhase { get; private set; }
    public void Initialize() => Status = SaveSlotManagerStatus.Idle;
    public void Reset(bool clearFailure = true)
    {
        CurrentOperation = null;
        Status = SaveSlotManagerStatus.Idle;
        if (clearFailure) { LastFailureMessage = null; LastFailurePhase = null; }
    }
    public SaveSlotOperationInfo StartOperation(
        SaveSlotId slot,
        SaveSlotManagerStatus status)
    {
        Status = status;
        LastFailureMessage = null;
        LastFailurePhase = null;
        CurrentOperation = new SaveSlotOperationInfo(
            Guid.NewGuid().ToString("N"),
            slot,
            status,
            SaveSlotOperationPhase.Eligibility,
            DateTimeOffset.UtcNow);
        logger.Info(
            "save",
            "save.operation_started",
            $"Save slot transaction '{CurrentOperation.TransactionId}' started for '{slot}' ({status}).");
        Publish(OperationStarted, CurrentOperation, "save.started_observer_failed");
        return CurrentOperation;
    }

    public SaveSlotOperationInfo RetargetOperation(
        SaveSlotOperationInfo operation,
        SaveSlotId target)
    {
        CurrentOperation = operation with { Slot = target };
        Publish(OperationProgressed, CurrentOperation, "save.progress_observer_failed");
        return CurrentOperation;
    }

    public void SetPhase(SaveSlotOperationInfo operation, SaveSlotOperationPhase phase)
    {
        CurrentOperation = operation with { Phase = phase };
        Publish(OperationProgressed, CurrentOperation, "save.progress_observer_failed");
        logger.Debug(
            "save",
            "save.operation_phase",
            $"Save slot transaction '{operation.TransactionId}' entered phase '{phase}'.");
    }

    public void CompleteOperation(SaveSlotOperationInfo operation)
    {
        CurrentOperation = operation with { Phase = SaveSlotOperationPhase.Completed };
        Status = SaveSlotManagerStatus.Idle;
        Publish(OperationCompleted, CurrentOperation, "save.completed_observer_failed");
        logger.Info(
            "save",
            "save.operation_completed",
            $"Save slot transaction '{operation.TransactionId}' completed for '{operation.Slot}'.");
    }

    public void CancelOperation()
    {
        CurrentOperation = null;
        Status = SaveSlotManagerStatus.Idle;
    }

    public void FailOperation(SaveSlotOperationInfo operation, Exception exception)
    {
        var phase = CurrentOperation?.Phase ?? operation.Phase;
        CurrentOperation = operation with
        {
            Phase = SaveSlotOperationPhase.Faulted,
            FailureMessage = exception.Message,
        };
        Status = SaveSlotManagerStatus.Faulted;
        LastFailureMessage = exception.Message;
        LastFailurePhase = phase;
        Publish(OperationFailed, CurrentOperation, "save.failed_observer_failed");
        logger.Error(
            "save",
            "save.operation_failed",
            $"Save slot transaction '{operation.TransactionId}' failed during '{phase}': {exception.Message}",
            exception);
    }

    public void PublishSlotsChanged() => Publish(
        SlotsChanged,
        "save.slots_changed_observer_failed");

    private void Publish(Action? observers, string eventId)
    {
        if (observers is null)
        {
            return;
        }
        foreach (Action observer in observers.GetInvocationList())
        {
            try
            {
                observer();
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "save",
                    eventId,
                    $"Save slot observer failed: {exception.Message}",
                    exception);
            }
        }
    }

    private void Publish<T>(Action<T>? observers, T value, string eventId)
    {
        if (observers is null)
        {
            return;
        }
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "save",
                    eventId,
                    $"Save slot observer failed: {exception.Message}",
                    exception);
            }
        }
    }

}

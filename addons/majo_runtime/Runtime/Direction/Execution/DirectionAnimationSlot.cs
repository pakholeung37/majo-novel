#nullable enable

using System;
using System.Threading.Tasks;

namespace Majo.Runtime.Direction.Execution;

public enum DirectionActionEnd { Completed, Superseded, Canceled, Failed }

/// <summary>A writer token owns a whole slot. Retired callbacks cannot write or finish a successor.</summary>
public sealed class DirectionAnimationSlot
{
    private DirectionSlotAction? current;
    public DirectionSlotAction? Current => current;

    public DirectionSlotAction Replace(Action<float> apply, Action? complete = null, bool finishPrevious = false)
    {
        if (finishPrevious) current?.Finish();
        else current?.End(DirectionActionEnd.Superseded);
        var action = new DirectionSlotAction(this, apply, complete);
        current = action;
        return action;
    }

    public void Finish() => current?.Finish();
    public void Cancel() => current?.End(DirectionActionEnd.Canceled);
    internal bool Owns(DirectionSlotAction action) => ReferenceEquals(current, action);
    internal void Retire(DirectionSlotAction action)
    {
        if (Owns(action)) current = null;
    }
}

public sealed class DirectionSlotAction
{
    private readonly DirectionAnimationSlot slot;
    private Action<float>? apply;
    private Action? complete;
    private IDisposable? driver;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => completion.Task;
    public DirectionActionEnd? EndReason { get; private set; }

    internal DirectionSlotAction(DirectionAnimationSlot slot, Action<float> apply, Action? complete)
    { this.slot = slot; this.apply = apply; this.complete = complete; }

    public void Attach(IDisposable value)
    {
        if (EndReason is not null) value.Dispose();
        else driver = value;
    }

    public void Apply(float progress)
    {
        if (EndReason is not null || !slot.Owns(this)) return;
        try { apply?.Invoke(progress); }
        catch (Exception error) { Fail(error); }
    }

    public void Finish()
    {
        if (EndReason is not null || !slot.Owns(this)) return;
        try
        {
            apply?.Invoke(1);
            slot.Retire(this);
            complete?.Invoke();
            End(DirectionActionEnd.Completed);
        }
        catch (Exception error) { Fail(error); }
    }

    public void Fail(Exception error)
    {
        if (EndReason is not null) return;
        EndReason = DirectionActionEnd.Failed;
        Release();
        completion.TrySetException(error);
    }

    public void End(DirectionActionEnd reason)
    {
        if (EndReason is not null) return;
        EndReason = reason;
        Release();
        completion.TrySetResult();
    }

    private void Release()
    {
        slot.Retire(this);
        try { driver?.Dispose(); }
        finally { driver = null; apply = null; complete = null; }
    }
}

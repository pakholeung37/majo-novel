#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;

namespace Majo.Runtime.UnitTests.Testing;

public class FakeOperationHandle : IOperationHandle
{
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeOperationHandle(OperationState initialState = OperationState.Running)
    {
        State = initialState;
        if (initialState == OperationState.Completed)
        {
            completion.SetResult();
        }
    }

    public OperationState State { get; private set; }
    public Task Completion => completion.Task;

    public void Cancel()
    {
        if (State == OperationState.Completed)
        {
            return;
        }

        State = OperationState.Cancelled;
        completion.TrySetCanceled();
        _ = completion.Task.Exception;
    }

    public void Finish()
    {
        State = OperationState.Completed;
        completion.TrySetResult();
    }
}

public class FakeActor : IActor
{
    public FakeActor(
        string id,
        string appearance = "default",
        string factoryId = "test")
    {
        Id = id;
        Appearance = TargetAppearance = appearance;
        FactoryId = factoryId;
    }

    public string Id { get; }
    public string FactoryId { get; }
    public string Appearance { get; set; }
    public string TargetAppearance { get; }
    public bool Visible { get; set; } = true;
    public bool TargetVisible => Visible;
    public Vector2 Position { get; set; }
    public Vector2 TargetPosition => Position;
    public Color Tint { get; set; } = Colors.White;
    public float Opacity { get; set; } = 1;
    public float TargetOpacity => Opacity;
    public int ZIndex { get; set; }
    public Node SceneNode => null!;
    public bool IsDestroyed { get; private set; }
    public FakeOperationHandle ActiveOperation { get; set; } = new();
    public FakeOperationHandle LastOperation { get; protected set; } = new();

    public virtual void SetAppearance(string appearance) => Appearance = appearance;
    public virtual Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual void SetVisible(bool visible) => Visible = visible;
    public virtual void SetPosition(Vector2 position) => Position = position;
    public virtual void SetTint(Color tint) => Tint = tint;
    public virtual void SetOpacity(float opacity) => Opacity = opacity;

    public virtual void SetZIndex(int zIndex)
    {
        ActorZIndexRange.Validate(zIndex);
        ZIndex = zIndex;
    }

    public virtual IOperationHandle ChangePosition(
        Vector2 position,
        double durationSeconds)
    {
        Position = position;
        return LastOperation = new FakeOperationHandle(OperationState.Completed);
    }

    public virtual IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds)
    {
        Opacity = opacity;
        return LastOperation = new FakeOperationHandle(OperationState.Completed);
    }

    public virtual IOperationHandle Show(
        double durationSeconds = 0)
    {
        Visible = true;
        return LastOperation = new FakeOperationHandle(OperationState.Completed);
    }

    public virtual IOperationHandle Hide(
        double durationSeconds = 0)
    {
        Visible = false;
        return LastOperation = new FakeOperationHandle(OperationState.Completed);
    }

    public virtual void Destroy()
    {
        IsDestroyed = true;
        ActiveOperation.Cancel();
    }
}

public class FakeActorFactory : IActorFactory
{
    public FakeActorFactory(string factoryId = "test")
    {
        FactoryId = factoryId;
    }

    public string FactoryId { get; }
    public int CreateCount { get; private set; }

    public virtual void ValidateDefinition(ActorDefinition definition) { }

    public virtual IActor Create(ActorDefinition definition)
    {
        CreateCount++;
        return new FakeActor(definition.Id, definition.DefaultAppearance, definition.FactoryId);
    }

    public virtual IActor Create(ActorDefinition definition, ActorCreationMode mode)
    {
        CreateCount++;
        var actor = new FakeActor(definition.Id, definition.DefaultAppearance, definition.FactoryId);
        actor.SetVisible(mode == ActorCreationMode.Active);
        return actor;
    }
}

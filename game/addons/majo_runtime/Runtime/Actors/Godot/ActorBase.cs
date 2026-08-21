#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Actors.Godot;

public abstract class ActorBase : IActor
{
    private const string PositionProperty = "position";
    private const string OpacityProperty = "opacity";
    private const string AppearanceProperty = "appearance";

    private readonly Dictionary<string, IOperationHandle> propertyOperations =
        new(StringComparer.Ordinal);
    private bool isDestroyed;

    protected ActorBase(
        ActorDefinition definition,
        CanvasItem view,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        View = view ?? throw new ArgumentNullException(nameof(view));
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        TextureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        Appearance = definition.DefaultAppearance;
    }

    protected ActorDefinition Definition { get; }

    protected CanvasItem View { get; }

    protected IOperationScheduler Scheduler { get; }

    protected IActorTextureLoader TextureLoader { get; }

    public string Id => Definition.Id;

    public string Appearance { get; protected set; }

    public bool Visible => View.Visible;

    public abstract Vector2 Position { get; }

    public Color Tint
    {
        get
        {
            var color = View.Modulate;
            return new Color(color.R, color.G, color.B, 1);
        }
    }

    public float Opacity => View.Modulate.A;

    public Node SceneNode => View;

    public void SetAppearance(string appearance)
    {
        EnsureAlive();
        var texture = TextureLoader.Load(Definition.GetAppearancePath(appearance));
        CancelProperty(AppearanceProperty);
        ApplyTexture(texture);
        Appearance = appearance;
    }

    public void SetVisible(bool visible)
    {
        EnsureAlive();
        View.Visible = visible;
    }

    public void SetPosition(Vector2 position)
    {
        EnsureAlive();
        ValidatePosition(position);
        CancelProperty(PositionProperty);
        ApplyPosition(position);
    }

    public void SetTint(Color tint)
    {
        EnsureAlive();
        var current = View.Modulate;
        View.Modulate = new Color(tint.R, tint.G, tint.B, current.A);
    }

    public void SetOpacity(float opacity)
    {
        EnsureAlive();
        ValidateOpacity(opacity);
        CancelProperty(OpacityProperty);
        var current = View.Modulate;
        View.Modulate = new Color(current.R, current.G, current.B, opacity);
    }

    public IOperationHandle ChangePosition(
        Vector2 position,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        ValidatePosition(position);
        var tween = GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("position"),
            position,
            durationSeconds);
        return StartSequencedOperation(
            PositionProperty, tween, operationId, groupId);
    }

    public IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        ValidateOpacity(opacity);
        var tween = GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("modulate:a"),
            opacity,
            durationSeconds);
        return StartSequencedOperation(
            OpacityProperty, tween, operationId, groupId);
    }

    public IOperationHandle Show(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        SetVisible(true);
        return ChangeOpacity(1, durationSeconds, operationId, groupId);
    }

    public IOperationHandle Hide(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        var tween = GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("modulate:a"),
            0.0f,
            durationSeconds);
        var visibilityOperation = new OperationDefinition(
            async context =>
            {
                await tween.ExecuteAsync(context);
                View.Visible = false;
            },
            completeAsync: async cancellationToken =>
            {
                await tween.CompleteAsync(cancellationToken);
                View.Visible = false;
            },
            cancelAsync: tween.CancelAsync);
        return StartSequencedOperation(
            OpacityProperty, visibilityOperation, operationId, groupId);
    }

    public virtual void Destroy()
    {
        if (isDestroyed)
        {
            return;
        }

        isDestroyed = true;
        foreach (var operation in propertyOperations.Values)
        {
            operation.Cancel();
        }

        propertyOperations.Clear();
        if (GodotObject.IsInstanceValid(View))
        {
            View.QueueFree();
        }
    }

    protected abstract void ApplyTexture(Texture2D texture);

    protected abstract void ApplyPosition(Vector2 position);

    protected IOperationHandle StartSequencedOperation(
        string property,
        OperationDefinition operation,
        string? operationId,
        string? groupId)
    {
        EnsureAlive();
        propertyOperations.TryGetValue(property, out var previous);
        if (previous is not null && IsTerminal(previous.State))
        {
            previous = null;
        }

        previous?.Complete();
        var sequenced = new OperationDefinition(
            async context =>
            {
                await WaitForPredecessorAsync(previous, context.CancellationToken);
                await operation.ExecuteAsync(context);
            },
            operationId,
            groupId,
            async cancellationToken =>
            {
                await WaitForPredecessorAsync(previous, cancellationToken);
                await operation.CompleteAsync(cancellationToken);
            },
            async cancellationToken =>
            {
                await WaitForPredecessorAsync(previous, cancellationToken);
                await operation.CancelAsync(cancellationToken);
            });
        var handle = Scheduler.Start(sequenced);
        propertyOperations[property] = handle;
        return handle;
    }

    protected void CancelPropertyAnimation(string property) => CancelProperty(property);

    private static async Task WaitForPredecessorAsync(
        IOperationHandle? predecessor,
        CancellationToken cancellationToken)
    {
        if (predecessor is null)
        {
            return;
        }

        try
        {
            await predecessor.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (
            predecessor.State == OperationState.Cancelled &&
            !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelProperty(string property)
    {
        if (propertyOperations.TryGetValue(property, out var operation) &&
            !IsTerminal(operation.State))
        {
            operation.Cancel();
        }
    }

    private void EnsureAlive()
    {
        if (isDestroyed || !GodotObject.IsInstanceValid(View))
        {
            throw new ActorException($"Actor '{Id}' has been destroyed.");
        }
    }

    private static void ValidateOpacity(float opacity)
    {
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opacity), "Opacity must be between zero and one.");
        }
    }

    private static void ValidatePosition(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), "Actor position must be finite.");
        }
    }

    private static bool IsTerminal(OperationState state) =>
        state is OperationState.Completed or OperationState.Cancelled or OperationState.Faulted;
}

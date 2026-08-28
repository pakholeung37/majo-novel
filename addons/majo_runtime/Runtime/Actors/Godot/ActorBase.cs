#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Actors.Godot;

public abstract class ActorBase : IActor, IGodotActorView
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
        : this(
            definition,
            view,
            scheduler,
            textureLoader,
            definition.DefaultAppearance)
    {
    }

    protected ActorBase(
        ActorDefinition definition,
        CanvasItem view,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        string initialAppearance)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        View = view ?? throw new ArgumentNullException(nameof(view));
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        TextureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialAppearance);
        Appearance = initialAppearance;
        TargetAppearance = Appearance;
        TargetVisible = view.Visible;
        TargetPosition = Vector2.Zero;
        TargetOpacity = view.Modulate.A;
    }

    protected ActorDefinition Definition { get; }

    protected CanvasItem View { get; }

    protected IOperationScheduler Scheduler { get; }

    protected IActorTextureLoader TextureLoader { get; }

    public string Id => Definition.Id;

    public string FactoryId => Definition.FactoryId;

    public string Appearance { get; protected set; }

    public string TargetAppearance { get; protected set; }

    public bool Visible => View.Visible;

    public bool TargetVisible { get; protected set; }

    public abstract Vector2 Position { get; }

    public Vector2 TargetPosition { get; protected set; }

    public Color Tint
    {
        get
        {
            var color = View.Modulate;
            return new Color(color.R, color.G, color.B, 1);
        }
    }

    public float Opacity => View.Modulate.A;

    public float TargetOpacity { get; protected set; }

    public Node SceneNode => View;

    public virtual void SetAppearance(string appearance)
    {
        EnsureAlive();
        var texture = TextureLoader.Load(
            Definition.GetAppearanceResourceKey(appearance),
            Definition.GetAppearanceOverride(appearance));
        CancelProperty(AppearanceProperty);
        ApplyTexture(texture);
        Appearance = appearance;
        TargetAppearance = appearance;
    }

    public void SetVisible(bool visible)
    {
        EnsureAlive();
        CancelProperty(OpacityProperty);
        View.Visible = visible;
        TargetVisible = visible;
    }

    public void SetPosition(Vector2 position)
    {
        EnsureAlive();
        ValidatePosition(position);
        CancelProperty(PositionProperty);
        ApplyPosition(position);
        TargetPosition = position;
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
        TargetOpacity = opacity;
    }

    public IOperationHandle ChangePosition(
        Vector2 position,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => ChangePosition(
            position,
            new AnimationSpec(durationSeconds),
            operationId,
            groupId);

    public IOperationHandle ChangePosition(
        Vector2 position,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        ValidatePosition(position);
        TargetPosition = position;
        var tween = GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("position"),
            position,
            animation.DurationSeconds,
            transition: GodotAnimationSpec.Transition(animation.Transition),
            ease: GodotAnimationSpec.Ease(animation.Ease),
            ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);
        return StartSequencedOperation(
            PositionProperty, tween, operationId, groupId, animation.ConflictPolicy);
    }

    public IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => ChangeOpacity(
            opacity,
            new AnimationSpec(durationSeconds),
            operationId,
            groupId);

    public IOperationHandle ChangeOpacity(
        float opacity,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        ValidateOpacity(opacity);
        TargetOpacity = opacity;
        var tween = GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("modulate:a"),
            opacity,
            animation.DurationSeconds,
            transition: GodotAnimationSpec.Transition(animation.Transition),
            ease: GodotAnimationSpec.Ease(animation.Ease),
            ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);
        return StartSequencedOperation(
            OpacityProperty, tween, operationId, groupId, animation.ConflictPolicy);
    }

    public IOperationHandle Show(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => Show(
            new AnimationSpec(durationSeconds), operationId, groupId);

    public IOperationHandle Show(
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        TargetVisible = true;
        TargetOpacity = 1;
        var tween = CreateOpacityTween(1, animation);
        var visibilityOperation = new OperationDefinition(
            async context =>
            {
                View.Visible = true;
                await tween.ExecuteAsync(context);
            },
            completeAsync: async cancellationToken =>
            {
                View.Visible = true;
                await tween.CompleteAsync(cancellationToken);
            },
            cancelAsync: tween.CancelAsync);
        return StartSequencedOperation(
            OpacityProperty,
            visibilityOperation,
            operationId,
            groupId,
            animation.ConflictPolicy);
    }

    public IOperationHandle Hide(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => Hide(
            new AnimationSpec(durationSeconds), operationId, groupId);

    public IOperationHandle Hide(
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        TargetVisible = false;
        TargetOpacity = 0;
        var tween = CreateOpacityTween(0, animation);
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
            OpacityProperty,
            visibilityOperation,
            operationId,
            groupId,
            animation.ConflictPolicy);
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
        string? groupId,
        AnimationConflictPolicy conflictPolicy = AnimationConflictPolicy.CompletePrevious)
    {
        EnsureAlive();
        propertyOperations.TryGetValue(property, out var previous);
        if (previous is not null && IsTerminal(previous.State))
        {
            previous = null;
        }

        if (conflictPolicy == AnimationConflictPolicy.CompletePrevious)
        {
            previous?.Complete();
        }
        else
        {
            previous?.Cancel();
        }
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
            },
            isSkippable: true);
        var handle = Scheduler.Start(sequenced);
        propertyOperations[property] = handle;
        return handle;
    }

    protected void CancelPropertyAnimation(string property) => CancelProperty(property);

    protected IOperationHandle FadeToAppearance(
        string appearance,
        Action applyAppearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        ArgumentNullException.ThrowIfNull(applyAppearance);
        var halfDuration = durationSeconds / 2;
        var fadeOut = CreateOpacityTween(0, new AnimationSpec(halfDuration));
        var fadeIn = CreateOpacityTween(1, new AnimationSpec(halfDuration));
        var appearanceApplied = false;

        void ApplyAppearance()
        {
            if (appearanceApplied)
            {
                return;
            }

            applyAppearance();
            Appearance = appearance;
            appearanceApplied = true;
        }

        TargetAppearance = appearance;
        TargetVisible = true;
        TargetOpacity = 1;
        var transition = new OperationDefinition(
            async context =>
            {
                View.Visible = true;
                await fadeOut.ExecuteAsync(context);
                ApplyAppearance();
                await fadeIn.ExecuteAsync(context);
            },
            completeAsync: async cancellationToken =>
            {
                View.Visible = true;
                await fadeOut.CompleteAsync(cancellationToken);
                ApplyAppearance();
                await fadeIn.CompleteAsync(cancellationToken);
            },
            cancelAsync: async cancellationToken =>
            {
                await fadeOut.CancelAsync(cancellationToken);
                await fadeIn.CancelAsync(cancellationToken);
                var color = View.Modulate;
                View.Modulate = new Color(color.R, color.G, color.B, 1);
                View.Visible = true;
                TargetAppearance = Appearance;
                TargetOpacity = 1;
                TargetVisible = true;
            },
            isSkippable: true);
        return StartSequencedOperation(
            OpacityProperty,
            transition,
            operationId,
            groupId);
    }

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

    private OperationDefinition CreateOpacityTween(float opacity, AnimationSpec animation) =>
        GodotTweenOperation.CreatePropertyTween(
            View,
            View,
            new NodePath("modulate:a"),
            opacity,
            animation.DurationSeconds,
            transition: GodotAnimationSpec.Transition(animation.Transition),
            ease: GodotAnimationSpec.Ease(animation.Ease),
            ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);
}

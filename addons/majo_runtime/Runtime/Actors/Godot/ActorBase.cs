#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.State;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

public abstract class ActorBase : IActor, IGodotActorView, IStateMutationParticipant
{
    private const string PositionProperty = "position";
    protected const string OpacityProperty = "opacity";
    protected const string AppearanceProperty = "appearance";

    private readonly Dictionary<string, IOperationHandle> propertyOperations =
        new(StringComparer.Ordinal);
    private readonly IEasingRegistry easings;
    private bool isDestroyed;
    private int visualPreparations;
    private StateMutationGate? mutationGate;

    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateCompatibilityException($"Actor '{Id}' is already bound to another runtime's state mutation gate.");
        mutationGate = gate;
    }

    public IDisposable RetainVisualPreparation()
    {
        visualPreparations++;
        return new VisualRetention(this);
    }

    private void ReleaseVisualPreparation()
    {
        if (--visualPreparations != 0 || isDestroyed || TextureLoader is not ResourceActorTextureLoader retained) return;
        var used = new HashSet<object>();
        void Keep(Texture2D texture)
        {
            if (!used.Add(texture)) return;
            if (texture is AtlasTexture { Atlas: { } atlas }) Keep(atlas);
        }
        void Visit(Node node)
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion()) return;
            if (node is Sprite2D { Texture: { } spriteTexture }) Keep(spriteTexture);
            if (node is TextureRect { Texture: { } rectTexture }) Keep(rectTexture);
            foreach (var child in node.GetChildren()) Visit(child);
        }
        Visit(View.GetParent() ?? View);
        retained.RetainOnly(used);
    }

    private sealed class VisualRetention(ActorBase actor) : IDisposable
    {
        private ActorBase? owner = actor;
        public void Dispose()
        {
            var value = owner;
            owner = null;
            value?.ReleaseVisualPreparation();
        }
    }

    protected IEasingRegistry Easings => easings;
    protected long AppearanceEpoch { get; private set; }
    protected void InvalidateAppearance() => AppearanceEpoch++;


    protected ActorBase(
        ActorDefinition definition,
        CanvasItem view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        IEasingRegistry? easings = null)
        : this(
            definition,
            view,
            runner,
            textureLoader,
            definition.DefaultAppearance,
            easings)
    {
    }

    protected ActorBase(
        ActorDefinition definition,
        CanvasItem view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        string initialAppearance,
        IEasingRegistry? easings = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        View = view ?? throw new ArgumentNullException(nameof(view));
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        TextureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialAppearance);
        Appearance = initialAppearance;
        TargetAppearance = Appearance;
        TargetVisible = view.Visible;
        TargetPosition = Vector2.Zero;
        TargetOpacity = view.Modulate.A;
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
    }


    protected ActorDefinition Definition { get; }

    protected CanvasItem View { get; }

    protected IOperationRunner Runner { get; }

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

    public virtual float Opacity => View.Modulate.A;

    public float TargetOpacity { get; protected set; }

    public int ZIndex => View.ZIndex;

    public Node SceneNode => View;

    public virtual void SetAppearance(string appearance)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            var texture = TextureLoader.Load(
                Definition.GetAppearanceResourceKey(appearance),
                Definition.GetAppearanceOverride(appearance));
            InvalidateAppearance();
            CancelProperty(AppearanceProperty);
            ApplyTexture(texture);
            Appearance = appearance;
            TargetAppearance = appearance;
        });
    }

    public virtual async Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        _ = await TextureLoader.LoadAsync(
            Definition.GetAppearanceResourceKey(appearance),
            Definition.GetAppearanceOverride(appearance),
            cancellationToken);
    }

    public virtual void SetVisible(bool visible)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            CancelProperty(OpacityProperty);
            View.Visible = visible;
            TargetVisible = visible;
        });
    }

    public void SetPosition(Vector2 position)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            ValidatePosition(position);
            CancelProperty(PositionProperty);
            ApplyPosition(position);
            TargetPosition = position;
        });
    }

    public void SetTint(Color tint)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            var current = View.Modulate;
            View.Modulate = new Color(tint.R, tint.G, tint.B, current.A);
        });
    }

    protected virtual void ApplyOpacity(float opacity)
    {
        var current = View.Modulate;
        View.Modulate = new Color(current.R, current.G, current.B, opacity);
    }

    public virtual void SetOpacity(float opacity)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            ValidateOpacity(opacity);
            CancelProperty(OpacityProperty);
            ApplyOpacity(opacity);
            TargetOpacity = opacity;
        });
    }

    public void SetZIndex(int zIndex)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            ActorZIndexRange.Validate(zIndex);
            View.ZIndex = zIndex;
        });
    }

    public IOperationHandle ChangePosition(
        Vector2 position,
        double durationSeconds) => ChangePosition(
            position,
            new AnimationSpec(durationSeconds));

    public IOperationHandle ChangePosition(
        Vector2 position,
        AnimationSpec animation)
    {
        return PersistentWrite(() =>
        {
            EnsureAlive();
            ValidatePosition(position);
            TargetPosition = position;
            var startPos = Position;
            var resolvedEasing = easings.GetRequired(animation.Easing);
            var operation = GodotTweenOperation.CreateProgressOperation(
                View,
                animation.Timing,
                resolvedEasing,
                applyProgress: e => ApplyPosition(startPos.Lerp(position, e)),
                finish: () => ApplyPosition(position),
                cancel: () => { });
            return StartSequencedOperation(
                PositionProperty, new OperationDefinition(
                    async context => { startPos = Position; await operation.ExecuteAsync(context); },
                    operation.FinishAsync, operation.CancelAsync), animation.ConflictPolicy);
        });
    }

    public virtual IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds) => ChangeOpacity(
            opacity,
            new AnimationSpec(durationSeconds));

    public virtual IOperationHandle ChangeOpacity(
        float opacity,
        AnimationSpec animation)
    {
        return PersistentWrite(() =>
        {
            EnsureAlive();
            ValidateOpacity(opacity);
            TargetOpacity = opacity;
            var startOpacity = Opacity;
            var resolvedEasing = easings.GetRequired(animation.Easing);

            var operation = GodotTweenOperation.CreateProgressOperation(
                View,
                animation.Timing,
                resolvedEasing,
                applyProgress: e =>
                {
                    var clamped = Math.Clamp(e, 0.0f, 1.0f);
                    ApplyOpacity(Mathf.Lerp(startOpacity, opacity, clamped));
                },
                finish: () => ApplyOpacity(opacity),
                cancel: () => { });
            return StartSequencedOperation(
                OpacityProperty, new OperationDefinition(
                    async context => { startOpacity = Opacity; await operation.ExecuteAsync(context); },
                    operation.FinishAsync, operation.CancelAsync), animation.ConflictPolicy);
        });
    }


    public virtual void Destroy()
    {
        PersistentWrite(() =>
        {
            if (isDestroyed) return;

            isDestroyed = true;
            InvalidateAppearance();
            foreach (var operation in propertyOperations.Values) operation.Cancel();

            propertyOperations.Clear();
            if (GodotObject.IsInstanceValid(View)) View.QueueFree();
        });
    }

    protected void PersistentWrite(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }

    protected T PersistentWrite<T>(Func<T> mutation) =>
        mutationGate is null ? mutation() : mutationGate.Write(mutation);

    protected abstract void ApplyTexture(Texture2D texture);

    protected abstract void ApplyPosition(Vector2 position);

    protected IOperationHandle StartSequencedOperation(
        string property,
        OperationDefinition operation,
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
            previous?.Finish();
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
            onFinish: async cancellationToken =>
            {
                await WaitForPredecessorAsync(previous, cancellationToken);
                await operation.FinishAsync(cancellationToken);
            },
            onCancel: async cancellationToken =>
            {
                await WaitForPredecessorAsync(previous, cancellationToken);
                await operation.CancelAsync(cancellationToken);
            });
        var handle = Runner.Start(sequenced);
        propertyOperations[property] = handle;
        return handle;
    }

    internal IOperationHandle TransitionVisual(ICharacterActor? source, ICharacterActor? target,
        AnimationSpec animation, ITransitionDriver driver)
    {
        EnsureAlive();
        var easing = Easings.GetRequired(animation.Easing);
        var owner = View.GetParent() as Node2D ?? throw new ActorException("Visual transition requires an actor root.");
        void ShowTarget()
        {
            if (source is not null && GodotObject.IsInstanceValid(source.View)) source.View.Visible = false;
            if (target is not null && GodotObject.IsInstanceValid(target.View)) target.View.Visible = true;
        }
        var operation = GodotActorAppearanceTransition.CreateOperation(owner, animation, easing, token =>
        {
            // The target tree is already fully prepared. Capture both actor-local trees in
            // the same stage coordinates, including their independent scale, position and tint.
            target?.SetOpacity(1);
            CanvasItem Copy(ICharacterActor? actor)
            {
                if (actor is null) return new Node2D();
                var copy = (Node2D)actor.View.Duplicate();
                copy.Transform = owner.GetGlobalTransform().AffineInverse() * actor.View.GetGlobalTransform();
                copy.Visible = true;
                return copy;
            }
            var from = Copy(source);
            var to = Copy(target);
            target?.SetVisible(false);
            return GodotActorAppearanceTransition.PrepareAsync(owner, from, to,
                owner.GetViewport().GetVisibleRect(), 1, visible =>
                {
                    if (visible) ShowTarget();
                    else
                    {
                        if (source is not null && GodotObject.IsInstanceValid(source.View)) source.View.Visible = false;
                        if (target is not null && GodotObject.IsInstanceValid(target.View)) target.View.Visible = false;
                    }
                }, _ => { }, driver, animation, token);
        }, ShowTarget);
        return StartSequencedOperation(AppearanceProperty, operation, animation.ConflictPolicy);
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

    protected void EnsureAlive()
    {
        if (isDestroyed || !GodotObject.IsInstanceValid(View))
        {
            throw new ActorException($"Actor '{Id}' has been destroyed.");
        }
    }

    protected bool IsAlive => !isDestroyed && GodotObject.IsInstanceValid(View);

    protected static void ValidateOpacity(float opacity)
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
        CreateOpacityTween(View, opacity, animation);

    private OperationDefinition CreateOpacityTween(
        CanvasItem target,
        float opacity,
        AnimationSpec animation)
    {
        var startOpacity = target.Modulate.A;
        var resolvedEasing = easings.GetRequired(animation.Easing);

        return GodotTweenOperation.CreateProgressOperation(
            View,
            animation.Timing,
            resolvedEasing,
            applyProgress: e =>
            {
                var clamped = Math.Clamp(e, 0.0f, 1.0f);
                var current = target.Modulate;
                target.Modulate = new Color(current.R, current.G, current.B, Mathf.Lerp(startOpacity, opacity, clamped));
            },
            finish: () =>
            {
                var current = target.Modulate;
                target.Modulate = new Color(current.R, current.G, current.B, opacity);
            },
            cancel: () => { });
    }
}

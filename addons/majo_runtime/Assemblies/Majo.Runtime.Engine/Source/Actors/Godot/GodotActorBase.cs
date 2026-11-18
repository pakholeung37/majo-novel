#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.State;

namespace Majo.Runtime.Actors.Godot;

/// <summary>
/// Godot actor lifecycle and immediate presentation state. Resource loading and
/// animation scheduling deliberately live outside this base class.
/// </summary>
public abstract class GodotActorBase : IActor, IGodotActorView, IStateMutationParticipant
{
    private bool isDestroyed;
    private StateMutationGate? mutationGate;

    protected GodotActorBase(ActorDefinition definition, CanvasItem view, string initialAppearance)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        View = view ?? throw new ArgumentNullException(nameof(view));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialAppearance);
        Appearance = initialAppearance;
    }

    protected ActorDefinition Definition { get; }
    protected CanvasItem View { get; }
    protected long AppearanceEpoch { get; private set; }
    protected bool IsAlive => !isDestroyed && GodotObject.IsInstanceValid(View);

    public string Id => Definition.Id;
    public string DefinitionId => Definition.DefinitionId;
    public string FactoryId => Definition.FactoryId;
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
    public virtual float Opacity => View.Modulate.A;
    public int ZIndex => View.ZIndex;
    public Node SceneNode => View;

    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateCompatibilityException(
                $"Actor '{Id}' is already bound to another runtime's state mutation gate.");
        mutationGate = gate;
    }

    public abstract void SetAppearance(string appearance);

    public virtual Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public virtual void SetVisible(bool visible) => PersistentWrite(() =>
    {
        EnsureAlive();
        View.Visible = visible;
    });

    public void SetPosition(Vector2 position) => PersistentWrite(() =>
    {
        EnsureAlive();
        ValidatePosition(position);
        ApplyPosition(position);
    });

    public void SetTint(Color tint) => PersistentWrite(() =>
    {
        EnsureAlive();
        var current = View.Modulate;
        View.Modulate = new Color(tint.R, tint.G, tint.B, current.A);
    });

    protected virtual void ApplyOpacity(float opacity)
    {
        var current = View.Modulate;
        View.Modulate = new Color(current.R, current.G, current.B, opacity);
    }

    public virtual void SetOpacity(float opacity) => PersistentWrite(() =>
    {
        EnsureAlive();
        ValidateOpacity(opacity);
        ApplyOpacity(opacity);
    });

    public void SetZIndex(int zIndex) => PersistentWrite(() =>
    {
        EnsureAlive();
        ActorZIndexRange.Validate(zIndex);
        View.ZIndex = zIndex;
    });

    public virtual void Destroy() => PersistentWrite(() =>
    {
        if (isDestroyed) return;
        isDestroyed = true;
        InvalidateAppearance();
        try { OnDestroy(); }
        finally { if (GodotObject.IsInstanceValid(View)) View.QueueFree(); }
    });

    protected void InvalidateAppearance() => AppearanceEpoch++;
    protected void EnsureAlive() => ObjectDisposedException.ThrowIf(!IsAlive, this);
    protected void PersistentWrite(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }
    protected T PersistentWrite<T>(Func<T> mutation) =>
        mutationGate is null ? mutation() : mutationGate.Write(mutation);
    protected abstract void ApplyPosition(Vector2 position);
    protected virtual void OnDestroy() { }

    protected static void ValidatePosition(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be finite.");
    }

    protected static void ValidateOpacity(float opacity)
    {
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between zero and one.");
    }

    protected static void ValidateScale(Vector2 scale)
    {
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || scale.X <= 0 || scale.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
    }
}

/// <summary>Common transform/preset implementation for stage character actors.</summary>
public abstract class GodotCharacterActorBase : GodotActorBase, ICharacterActor
{
    private readonly CharacterPositionMap positions;

    protected GodotCharacterActorBase(
        ActorDefinition definition,
        Node2D view,
        CharacterPositionMap positions,
        CharacterPositionPreset initialPosition,
        string initialAppearance)
        : base(definition, view, initialAppearance)
    {
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        PositionPreset = initialPosition;
        ApplyPosition(positions.Get(initialPosition));
    }

    public new Node2D View => (Node2D)base.View;
    CanvasItem ICharacterActor.View => View;
    public override Vector2 Position => View.Position;
    public Vector2 Scale => View.Scale;
    public Vector2 ScenePosition => positions.GetScenePercent(Position);
    public float RotationDegrees => View.RotationDegrees;
    public CharacterPositionPreset PositionPreset { get; private set; }

    public virtual string ResolveAppearance(string sourceAppearance, string appearance) => appearance;

    public virtual IDisposable? RetainVisualPreparation() => null;

    public void SetRotationDegrees(float rotation) => PersistentWrite(() =>
    {
        EnsureAlive();
        if (!float.IsFinite(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
        View.RotationDegrees = rotation;
    });

    public void SetScale(Vector2 scale) => PersistentWrite(() =>
    {
        EnsureAlive();
        ValidateScale(scale);
        View.Scale = scale;
    });

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(positions.GetScenePosition(scenePosition));

    public void SetPosition(CharacterPositionPreset preset) => PersistentWrite(() =>
    {
        EnsureAlive();
        PositionPreset = preset;
        ApplyPosition(positions.Get(preset));
    });

    protected override void ApplyPosition(Vector2 position) => View.Position = position;

    public abstract Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance,
        string appearance,
        AnimationSpec animation,
        ITransitionDriver driver,
        CancellationToken cancellationToken = default);
}

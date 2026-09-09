#nullable enable

using System;
using System.Threading.Tasks;
using System.Threading;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterActor : ActorBase, ICharacterActor, IActorTransformTarget, IActorAppearanceTarget, IActorVisibilityTarget
{
    private readonly CharacterPositionMap positions;
    private readonly ITransitionDriverRegistry transitions;

    public CharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        IEasingRegistry? easings = null, ITransitionDriverRegistry? transitions = null)
        : base(definition, view, runner, textureLoader, definition.DefaultAppearance, easings)
    {

        this.transitions = transitions ?? new TransitionDriverRegistry();
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        PositionPreset = definition.InitialPosition;
        ApplyPosition(positions.Get(PositionPreset));
        TargetPosition = Position;
        SetAppearance(definition.DefaultAppearance);
    }

    public new Sprite2D View => (Sprite2D)base.View;
    CanvasItem ICharacterActor.View => View;

    public override Vector2 Position => View.Position;

    public Vector2 Scale => View.Scale;
    public Vector2 ScenePosition => positions.GetScenePercent(Position);
    public float RotationDegrees => View.RotationDegrees;
    public void SetRotationDegrees(float rotation)
    {
        PersistentWrite(() =>
        {
            if (!float.IsFinite(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
            View.RotationDegrees = rotation;
        });
    }

    public CharacterPositionPreset PositionPreset { get; private set; }

    public void SetScale(Vector2 scale)
    {
        PersistentWrite(() =>
        {
            ValidateScale(scale);
            View.Scale = scale;
        });
    }

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(positions.GetScenePosition(scenePosition));

    public void SetPosition(CharacterPositionPreset preset)
    {
        PersistentWrite(() =>
        {
            PositionPreset = preset;
            SetPosition(positions.Get(preset));
        });
    }

    public IOperationHandle ChangeAppearance(
        string appearance,
        double durationSeconds) =>
        ChangeAppearance(appearance, new AnimationSpec(durationSeconds));

    public IOperationHandle ChangeAppearance(string appearance, AnimationSpec animation) =>
        ChangeAppearance(appearance, animation, transitions.GetRequired(DirectionTransitionId.Dissolve));

    public IOperationHandle ChangeAppearance(string appearance, AnimationSpec animation, ITransitionDriver driver)
    {
        return PersistentWrite(() =>
        {
            EnsureAlive();
            ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
            var easing = Easings.GetRequired(animation.Easing);
            var texture = TextureLoader.Load(Definition.GetAppearanceResourceKey(appearance),
                Definition.GetAppearanceOverride(appearance));
            var epoch = AppearanceEpoch;
            TargetAppearance = appearance;
            var operation = GodotActorAppearanceTransition.CreateOperation(View, animation, easing,
            token =>
            {
                var original = View.Texture;
                var source = CreateAppearanceSprite(original);
                var target = CreateAppearanceSprite(texture);
                var bounds = source.GetRect().Merge(target.GetRect());
                return GodotActorAppearanceTransition.PrepareAsync(View, source, target, bounds, 1,
                    visible => { if (IsAlive && AppearanceEpoch == epoch) View.Texture = visible ? original : null; },
                    _ => { }, driver, animation, token);
            },
            () =>
            {
                if (!IsAlive || AppearanceEpoch != epoch) return;
                ApplyTexture(texture);
                Appearance = appearance;
            });
            return StartSequencedOperation(AppearanceProperty, operation, animation.ConflictPolicy);
        });
    }

    public async Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(string appearance,
        AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        var sourceTexture = await TextureLoader.LoadAsync(Definition.GetAppearanceResourceKey(TargetAppearance),
            Definition.GetAppearanceOverride(TargetAppearance), cancellationToken);
        var texture = await TextureLoader.LoadAsync(Definition.GetAppearanceResourceKey(appearance),
            Definition.GetAppearanceOverride(appearance), cancellationToken);
        var source = CreateAppearanceSprite(sourceTexture);
        var target = CreateAppearanceSprite(texture);
        return await GodotActorAppearanceTransition.PrepareForCommitAsync(View, source, target,
            source.GetRect().Merge(target.GetRect()),
            visible => { if (IsAlive) View.Texture = visible ? sourceTexture : null; },
            _ => { }, () => TargetAppearance = appearance, () => SetAppearance(appearance),
            driver, animation, cancellationToken);
    }

    private Sprite2D CreateAppearanceSprite(Texture2D? texture) => new()
    {
        Texture = texture,
        Offset = View.Offset,
        Centered = View.Centered,
        FlipH = View.FlipH,
        FlipV = View.FlipV,
    };

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        double durationSeconds) => ChangePosition(
            preset,
            new AnimationSpec(durationSeconds));

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        AnimationSpec animation)
    {
        return PersistentWrite(() =>
        {
            PositionPreset = preset;
            return ChangePosition(positions.Get(preset), animation);
        });
    }

    protected override void ApplyTexture(Texture2D texture)
    {
        View.Texture = texture;
    }

    protected override void ApplyPosition(Vector2 position)
    {
        View.Position = position;
    }

    private static void ValidateScale(Vector2 scale)
    {
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) ||
            scale.X <= 0 || scale.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        }
    }
}

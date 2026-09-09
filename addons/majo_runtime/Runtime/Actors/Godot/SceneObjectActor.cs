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

public sealed class SceneObjectActor : ActorBase, IActorTransformTarget, IActorAppearanceTarget, IActorVisibilityTarget
{
    private readonly Vector2I resolution;
    private readonly ITransitionDriverRegistry transitions;

    public SceneObjectActor(
        ActorDefinition definition,
        Sprite2D view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        Vector2I resolution,
        IEasingRegistry? easings = null, ITransitionDriverRegistry? transitions = null)
        : base(definition, view, runner, textureLoader, definition.DefaultAppearance, easings)
    {

        this.transitions = transitions ?? new TransitionDriverRegistry();
        this.resolution = resolution;
        ApplyPosition(StageCoordinates.ToPixels(new Vector2(50, 0), resolution));
        TargetPosition = Position;
        SetAppearance(definition.DefaultAppearance);
    }

    public new Sprite2D View => (Sprite2D)base.View;

    public override Vector2 Position => View.Position;

    public Vector2 Scale => View.Scale;
    public Vector2 ScenePosition => StageCoordinates.ToPercent(Position, resolution);
    public float RotationDegrees => View.RotationDegrees;
    public void SetRotationDegrees(float rotation)
    {
        PersistentWrite(() =>
        {
            if (!float.IsFinite(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
            View.RotationDegrees = rotation;
        });
    }


    public void SetScale(Vector2 scale)
    {
        PersistentWrite(() =>
        {
            ValidateScale(scale);
            View.Scale = scale;
        });
    }

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(StageCoordinates.ToPixels(scenePosition, resolution));

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
        Offset = (new Vector2(.5f, .5f) - Definition.GetFactoryData<TextureActorDefinitionData>().Anchor) * (texture?.GetSize() ?? Vector2.Zero),
        Centered = View.Centered,
        FlipH = View.FlipH,
        FlipV = View.FlipV,
    };

    public override void SetAppearance(string appearance)
    {
        ValidateAppearance(appearance);
        base.SetAppearance(appearance);
    }

    public override Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default)
    {
        ValidateAppearance(appearance);
        return base.PrepareAppearanceAsync(appearance, cancellationToken);
    }

    private void ValidateAppearance(string appearance)
    {
        if (!Definition.Appearances.ContainsKey(appearance)) throw new UnknownActorAppearanceException(Id, appearance);
    }

    protected override void ApplyTexture(Texture2D texture)
    {
        View.Texture = texture;
        View.Offset = (new Vector2(0.5f, 0.5f) - Definition.GetFactoryData<TextureActorDefinitionData>().Anchor) * texture.GetSize();
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

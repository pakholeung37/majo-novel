#nullable enable

using System;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterActor : ActorBase, ICharacterActor
{
    private readonly CharacterPositionMap positions;

    public CharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions)
        : base(definition, view, runner, textureLoader)
    {
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

    public CharacterPositionPreset PositionPreset { get; private set; }

    public void SetScale(Vector2 scale)
    {
        ValidateScale(scale);
        View.Scale = scale;
    }

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(positions.GetScenePosition(scenePosition));

    public void SetPosition(CharacterPositionPreset preset)
    {
        PositionPreset = preset;
        SetPosition(positions.Get(preset));
    }

    public IOperationHandle ChangeAppearance(
        string appearance,
        double durationSeconds)
    {
        var texture = TextureLoader.Load(
            Definition.GetAppearanceResourceKey(appearance),
            Definition.GetAppearanceOverride(appearance));
        return CrossFadeToAppearance(
            appearance,
            () => ApplyTexture(texture),
            durationSeconds);
    }

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        double durationSeconds) => ChangePosition(
            preset,
            new AnimationSpec(durationSeconds));

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        AnimationSpec animation)
    {
        PositionPreset = preset;
        return ChangePosition(
            positions.Get(preset), animation);
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

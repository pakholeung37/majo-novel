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
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions)
        : base(definition, view, scheduler, textureLoader)
    {
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        PositionPreset = definition.InitialPosition;
        ApplyPosition(positions.Get(PositionPreset));
        TargetPosition = Position;
        SetAppearance(definition.DefaultAppearance);
    }

    public new Sprite2D View => (Sprite2D)base.View;

    public override Vector2 Position => View.Position;

    public CharacterPositionPreset PositionPreset { get; private set; }

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => ChangePosition(
            preset,
            new AnimationSpec(durationSeconds),
            operationId,
            groupId);

    public IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        PositionPreset = preset;
        return ChangePosition(
            positions.Get(preset), animation, operationId, groupId);
    }

    protected override void ApplyTexture(Texture2D texture)
    {
        View.Texture = texture;
    }

    protected override void ApplyPosition(Vector2 position)
    {
        View.Position = position;
    }
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors;

public static class BackgroundSlotIds
{
    public const string Main = "main";

    public static string Normalize(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
        {
            throw new ActorException("Background slot cannot be empty.");
        }

        return slot.Trim().ToLowerInvariant();
    }
}

public static class ActorZIndexRange
{
    public const int Minimum = -4096;

    public const int Maximum = 4096;

    public static void Validate(int zIndex)
    {
        if (zIndex is < Minimum or > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zIndex),
                $"Actor z-index must be between {Minimum} and {Maximum}.");
        }
    }
}

public interface IActor
{
    string Id { get; }

    string DefinitionId { get; }

    string FactoryId { get; }

    string Appearance { get; }

    bool Visible { get; }

    Vector2 Position { get; }

    Color Tint { get; }

    float Opacity { get; }

    int ZIndex { get; }

    void SetAppearance(string appearance);

    Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    void SetVisible(bool visible);

    void SetPosition(Vector2 position);

    void SetTint(Color tint);

    void SetOpacity(float opacity);

    void SetZIndex(int zIndex);

    void Destroy();
}

public interface IGodotActorView
{
    Node SceneNode { get; }
}

public interface ICharacterActor : ISceneActor
{
    CanvasItem View { get; }

    CharacterPositionPreset PositionPreset { get; }

    void SetPosition(CharacterPositionPreset preset);

}

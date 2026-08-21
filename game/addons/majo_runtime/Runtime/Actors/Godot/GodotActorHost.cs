using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Actors.Godot;

internal sealed class GodotActorHost : IRuntimeService
{
    private readonly Node owner;
    private bool isInitialized;

    public GodotActorHost(Node owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Root = new CanvasLayer { Name = "MajoActors" };
        BackgroundLayer = new Control
        {
            Name = "Backgrounds",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        CharacterLayer = new Node2D { Name = "Characters" };
        Root.AddChild(BackgroundLayer);
        Root.AddChild(CharacterLayer);
    }

    public CanvasLayer Root { get; }

    public Control BackgroundLayer { get; }

    public Node2D CharacterLayer { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        owner.AddChild(Root);
        BackgroundLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized && GodotObject.IsInstanceValid(Root))
        {
            Root.QueueFree();
        }

        isInitialized = false;
        return Task.CompletedTask;
    }
}

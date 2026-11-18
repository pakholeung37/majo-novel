#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Actors.Godot;

/// <summary>
/// Composable texture loading and retained-scope support. It owns neither the
/// loader nor the actor node.
/// </summary>
public sealed class TextureAppearanceSupport
{
    private readonly IActorTextureLoader loader;
    private readonly CanvasItem view;
    private readonly TextureActorDefinitionData? definition;
    private readonly string definitionId;
    private int visualPreparations;

    public TextureAppearanceSupport(
        IActorTextureLoader loader,
        CanvasItem view,
        TextureActorDefinitionData? definition = null,
        string? definitionId = null)
    {
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.definition = definition;
        this.definitionId = definitionId?.Trim() ?? string.Empty;
        if (definition is not null) ArgumentException.ThrowIfNullOrWhiteSpace(this.definitionId);
    }

    public IActorTextureLoader Loader => loader;

    public Texture2D Load(string appearance)
    {
        var data = GetDefinition();
        return loader.Load(
            data.GetAppearanceResourceKey(definitionId, appearance),
            data.GetAppearanceOverride(appearance));
    }

    public Task<Texture2D> LoadAsync(string appearance, CancellationToken cancellationToken = default)
    {
        var data = GetDefinition();
        return loader.LoadAsync(
            data.GetAppearanceResourceKey(definitionId, appearance),
            data.GetAppearanceOverride(appearance),
            cancellationToken);
    }

    public IDisposable RetainVisualPreparation()
    {
        visualPreparations++;
        return new VisualRetention(this);
    }

    private TextureActorDefinitionData GetDefinition() => definition ??
        throw new InvalidOperationException("This texture support instance has no texture actor definition.");

    private void ReleaseVisualPreparation()
    {
        if (--visualPreparations != 0 || loader is not ResourceActorTextureLoader retained) return;
        if (!GodotObject.IsInstanceValid(view) || view.IsQueuedForDeletion()) return;
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
        Visit(view.GetParent() ?? view);
        retained.RetainOnly(used);
    }

    private sealed class VisualRetention(TextureAppearanceSupport owner) : IDisposable
    {
        private TextureAppearanceSupport? owner = owner;
        public void Dispose()
        {
            var value = owner;
            owner = null;
            value?.ReleaseVisualPreparation();
        }
    }
}

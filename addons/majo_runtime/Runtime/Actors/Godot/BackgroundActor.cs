#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActor : GodotBackgroundActorBase
{
    private readonly TextureActorDefinitionData data;
    private readonly IActorTextureLoader loader;

    public BackgroundActor(ActorDefinition definition, Node2D container, IActorTextureLoader textureLoader,
        bool initializeAppearance = true)
        : base(definition, container, definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance)
    {
        data = definition.GetFactoryData<TextureActorDefinitionData>();
        loader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        if (initializeAppearance) SetAppearance(data.DefaultAppearance);
    }

    protected override GodotBackgroundContent LoadContent(string appearance)
    {
        var scope = (loader as ResourceActorTextureLoader)?.CreateContentScope();
        try
        {
            var texture = (scope ?? loader).Load(data.GetAppearanceResourceKey(DefinitionId, appearance), data.GetAppearanceOverride(appearance));
            return CreateContent(texture, scope);
        }
        catch { scope?.Dispose(); throw; }
    }

    protected override async Task<GodotBackgroundContent> LoadContentAsync(string appearance, CancellationToken token)
    {
        var scope = (loader as ResourceActorTextureLoader)?.CreateContentScope();
        try
        {
            var texture = await (scope ?? loader).LoadAsync(data.GetAppearanceResourceKey(DefinitionId, appearance),
                data.GetAppearanceOverride(appearance), token);
            token.ThrowIfCancellationRequested();
            return CreateContent(texture, scope);
        }
        catch { scope?.Dispose(); throw; }
    }

    private GodotBackgroundContent CreateContent(Texture2D texture, IDisposable? scope) => OwnContent(
        new Sprite2D { Name = "Appearance", Texture = texture, Centered = false },
        new Rect2(Vector2.Zero, texture.GetSize()),
        texture.HasAlpha() ? BackgroundCoverage.Unknown : BackgroundCoverage.OpaqueRectangle, scope);
}

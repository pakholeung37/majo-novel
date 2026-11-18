#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Actors.Godot;

public sealed class TextureCharacterActor : GodotCharacterActorBase
{
    private readonly TextureActorDefinitionData textureDefinition;
    private readonly TextureAppearanceSupport textures;
    private readonly IActorAppearanceTransitionService transitions;

    public TextureCharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        IActorAppearanceTransitionService? transitions = null)
        : this(definition, view, textureLoader, positions,
            definition.GetFactoryData<TextureActorDefinitionData>(), transitions)
    {
    }

    private TextureCharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        TextureActorDefinitionData textureDefinition,
        IActorAppearanceTransitionService? transitions)
        : base(definition, view, positions, textureDefinition.InitialPosition,
            textureDefinition.DefaultAppearance)
    {
        this.textureDefinition = textureDefinition;
        textures = new TextureAppearanceSupport(textureLoader, view, textureDefinition, definition.DefinitionId);
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
        SetAppearance(textureDefinition.DefaultAppearance);
    }

    public new Sprite2D View => (Sprite2D)base.View;

    public override void SetAppearance(string appearance)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            var texture = textures.Load(appearance);
            InvalidateAppearance();
            View.Texture = texture;
            Appearance = appearance;
        });
    }

    public override async Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        _ = await textures.Loader.LoadAsync(
            textureDefinition.GetAppearanceResourceKey(DefinitionId, appearance),
            textureDefinition.GetAppearanceOverride(appearance),
            cancellationToken);
    }

    public override IDisposable RetainVisualPreparation() => textures.RetainVisualPreparation();

    public override async Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance,
        string appearance,
        AnimationSpec animation,
        ITransitionDriver driver,
        CancellationToken cancellationToken = default)
    {
        var sourceTexture = await textures.Loader.LoadAsync(
            textureDefinition.GetAppearanceResourceKey(DefinitionId, sourceAppearance),
            textureDefinition.GetAppearanceOverride(sourceAppearance), cancellationToken);
        var texture = await textures.Loader.LoadAsync(
            textureDefinition.GetAppearanceResourceKey(DefinitionId, appearance),
            textureDefinition.GetAppearanceOverride(appearance), cancellationToken);
        var source = CreateAppearanceSprite(sourceTexture);
        var target = CreateAppearanceSprite(texture);
        return await transitions.PrepareForCommitAsync(View, source, target,
            source.GetRect().Merge(target.GetRect()),
            visible => { if (IsAlive) View.Texture = visible ? sourceTexture : null; },
            _ => { }, () => { }, () => SetAppearance(appearance), appearance,
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
}

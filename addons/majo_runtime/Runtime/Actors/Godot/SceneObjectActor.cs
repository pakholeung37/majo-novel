#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Actors.Godot;

public sealed class SceneObjectActor : GodotActorBase, ISceneActor
{
    private readonly Vector2I resolution;
    private readonly TextureActorDefinitionData textureDefinition;
    private readonly TextureAppearanceSupport textures;
    private readonly IActorAppearanceTransitionService transitions;

    public SceneObjectActor(ActorDefinition definition, Sprite2D view,
        IActorTextureLoader textureLoader, Vector2I resolution,
        IActorAppearanceTransitionService? transitions = null)
        : this(definition, view, textureLoader, resolution,
            definition.GetFactoryData<TextureActorDefinitionData>(), transitions)
    { }

    private SceneObjectActor(ActorDefinition definition, Sprite2D view,
        IActorTextureLoader textureLoader, Vector2I resolution,
        TextureActorDefinitionData textureDefinition,
        IActorAppearanceTransitionService? transitions)
        : base(definition, view, textureDefinition.DefaultAppearance)
    {
        this.resolution = resolution;
        this.textureDefinition = textureDefinition;
        textures = new TextureAppearanceSupport(textureLoader, view, textureDefinition, definition.DefinitionId);
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
        ApplyPosition(StageCoordinates.ToPixels(new Vector2(50, 0), resolution));
        SetAppearance(textureDefinition.DefaultAppearance);
    }

    public new Sprite2D View => (Sprite2D)base.View;
    public override Vector2 Position => View.Position;
    public Vector2 Scale => View.Scale;
    public Vector2 ScenePosition => StageCoordinates.ToPercent(Position, resolution);
    public float RotationDegrees => View.RotationDegrees;
    public IDisposable RetainVisualPreparation() => textures.RetainVisualPreparation();

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
        SetPosition(StageCoordinates.ToPixels(scenePosition, resolution));

    public override void SetAppearance(string appearance)
    {
        ValidateAppearance(appearance);
        PersistentWrite(() =>
        {
            EnsureAlive();
            var texture = textures.Load(appearance);
            InvalidateAppearance();
            View.Texture = texture;
            View.Offset = (new Vector2(.5f, .5f) - textureDefinition.Anchor) * texture.GetSize();
            Appearance = appearance;
        });
    }

    public override async Task PrepareAppearanceAsync(string appearance,
        CancellationToken cancellationToken = default)
    {
        ValidateAppearance(appearance);
        _ = await textures.Loader.LoadAsync(
            textureDefinition.GetAppearanceResourceKey(DefinitionId, appearance),
            textureDefinition.GetAppearanceOverride(appearance), cancellationToken);
    }

    public async Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance, string appearance, AnimationSpec animation,
        ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        ValidateAppearance(sourceAppearance);
        ValidateAppearance(appearance);
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
        Offset = (new Vector2(.5f, .5f) - textureDefinition.Anchor) * (texture?.GetSize() ?? Vector2.Zero),
        Centered = View.Centered,
        FlipH = View.FlipH,
        FlipV = View.FlipV,
    };

    private void ValidateAppearance(string appearance)
    {
        if (!textureDefinition.Appearances.ContainsKey(appearance))
            throw new UnknownActorAppearanceException(Id, appearance);
    }

    protected override void ApplyPosition(Vector2 position) => View.Position = position;
}

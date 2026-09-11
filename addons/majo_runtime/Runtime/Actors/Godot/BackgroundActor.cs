#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActor : GodotActorBase, IBackgroundActor
{
    private readonly TextureActorDefinitionData textureDefinition;
    private readonly TextureAppearanceSupport textures;

    public BackgroundActor(ActorDefinition definition, Control container,
        IActorTextureLoader textureLoader)
        : this(definition, container, textureLoader,
            definition.GetFactoryData<TextureActorDefinitionData>())
    { }

    private BackgroundActor(ActorDefinition definition, Control container,
        IActorTextureLoader textureLoader, TextureActorDefinitionData textureDefinition)
        : base(definition, container, textureDefinition.DefaultAppearance)
    {
        this.textureDefinition = textureDefinition;
        textures = new TextureAppearanceSupport(textureLoader, container, textureDefinition, definition.DefinitionId);
        Container.Resized += LayoutViews;
        CurrentView = CreateView();
        SetAppearance(textureDefinition.DefaultAppearance);
    }

    private Control Container => (Control)base.View;
    public TextureRect CurrentView { get; private set; }
    public string Slot { get; private set; } = BackgroundSlotIds.Main;
    public override Vector2 Position => Container.Position;

    public void SetSlot(string slot) =>
        PersistentWrite(() => Slot = BackgroundSlotIds.Normalize(slot));

    public override void SetAppearance(string appearance) => PersistentWrite(() =>
    {
        EnsureAlive();
        var texture = textures.Load(appearance);
        InvalidateAppearance();
        CurrentView.Texture = texture;
        LayoutView(CurrentView);
        Appearance = appearance;
    });

    public override async Task PrepareAppearanceAsync(string appearance,
        CancellationToken cancellationToken = default)
    {
        _ = await textures.Loader.LoadAsync(
            textureDefinition.GetAppearanceResourceKey(DefinitionId, appearance),
            textureDefinition.GetAppearanceOverride(appearance), cancellationToken);
    }

    protected override void ApplyPosition(Vector2 position) => Container.Position = position;

    private TextureRect CreateView()
    {
        var view = new TextureRect
        {
            Name = "Appearance",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Container.AddChild(view);
        return view;
    }

    private void LayoutViews()
    {
        foreach (var child in Container.GetChildren())
            if (child is TextureRect view) LayoutView(view);
    }

    private void LayoutView(TextureRect view)
    {
        var textureSize = view.Texture?.GetSize() ?? Vector2.Zero;
        var viewportSize = Container.Size;
        if (textureSize.X <= 0 || textureSize.Y <= 0 || viewportSize.X <= 0 || viewportSize.Y <= 0) return;
        var scale = Mathf.Max(viewportSize.X / textureSize.X, viewportSize.Y / textureSize.Y);
        var renderedSize = textureSize * scale;
        view.AnchorLeft = 0.5f;
        view.AnchorTop = 0.5f;
        view.AnchorRight = 0.5f;
        view.AnchorBottom = 0.5f;
        view.OffsetLeft = -renderedSize.X / 2;
        view.OffsetTop = -renderedSize.Y / 2;
        view.OffsetRight = renderedSize.X / 2;
        view.OffsetBottom = renderedSize.Y / 2;
    }
}

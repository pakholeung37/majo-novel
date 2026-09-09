#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class LayeredCharacterActor : ActorBase, ICharacterActor, IActorTransformTarget, IActorAppearanceTarget, IActorVisibilityTarget, IDisposable
{
    private const string GroupOpacityParameter = "group_opacity";
    private const ulong LayerBuildBudgetMicroseconds = 2_000;
    private const string AlphaAwareMultiplyShaderPath =
        "res://addons/majo_runtime/Runtime/Actors/Godot/Shaders/AlphaAwareMultiply.gdshader";
    private const string LayeredCompositeShaderPath =
        "res://addons/majo_runtime/Runtime/Actors/Godot/Shaders/LayeredComposite.gdshader";
    private readonly CharacterPositionMap positions;
    private readonly ITransitionDriverRegistry transitions;
    private readonly LayerCompositionResolver resolver;
    private readonly HashSet<string> activeLayers;
    private readonly HashSet<string> targetLayers;
    private readonly ShaderMaterial multiplyMaterial;
    private readonly CanvasGroup layerComposite;
    private readonly Dictionary<string, Sprite2D> layerViews =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nodeNameToLayerPath =
        new(StringComparer.Ordinal);
    private Rect2 canvasBounds;
    private Sprite2D? activeOverlay;

    public LayeredCharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        LayeredCharacterManifest manifest,
        string manifestPath,
        CharacterPositionPreset initialPosition,
        ShaderMaterial? multiplyMaterial = null,
        bool buildLayerViews = true,
        Majo.Runtime.Timing.IEasingRegistry? easings = null, ITransitionDriverRegistry? transitions = null)
        : base(
            definition,
            view,
            runner,
            textureLoader,
            manifest.DefaultAppearance,
            easings)
    {

        this.transitions = transitions ?? new TransitionDriverRegistry();
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        this.multiplyMaterial = multiplyMaterial ?? CreateAlphaAwareMultiplyMaterial();
        layerComposite = new CanvasGroup
        {
            Name = "LayerComposite",
            Material = CreateLayeredCompositeMaterial(),
        };
        View.AddChild(layerComposite);
        SetCompositeOpacity(1);
        resolver = new LayerCompositionResolver(manifest);
        activeLayers = resolver.CreateDefaultState();
        targetLayers = new HashSet<string>(activeLayers, StringComparer.Ordinal);
        if (buildLayerViews)
        {
            BuildLayerViews(manifest, manifestPath, null);
            ApplyLayerVisibility(activeLayers);
        }
        Appearance = resolver.Canonicalize(activeLayers);
        TargetAppearance = Appearance;
        PositionPreset = initialPosition;
        ApplyPosition(positions.Get(PositionPreset));
        TargetPosition = Position;
    }

    public new Sprite2D View => (Sprite2D)base.View;
    CanvasItem ICharacterActor.View => View;

    public override Vector2 Position => View.Position;

    public override float Opacity =>
        (float)layerComposite.GetInstanceShaderParameter(GroupOpacityParameter);

    public Vector2 Scale => View.Scale;
    public Vector2 ScenePosition => positions.GetScenePercent(Position);
    public float RotationDegrees => View.RotationDegrees;
    public void SetRotationDegrees(float rotation)
    {
        PersistentWrite(() =>
        {
            if (!float.IsFinite(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
            View.RotationDegrees = rotation;
        });
    }

    public CharacterPositionPreset PositionPreset { get; private set; }

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(positions.GetScenePosition(scenePosition));

    public void SetPosition(CharacterPositionPreset preset)
    {
        PersistentWrite(() =>
        {
            PositionPreset = preset;
            SetPosition(positions.Get(preset));
        });
    }

    protected override void ApplyOpacity(float opacity)
    {
        SetCompositeOpacity(opacity);
        if (activeOverlay is not null && GodotObject.IsInstanceValid(activeOverlay))
        {
            activeOverlay.Modulate = new Color(1, 1, 1, opacity);
        }
    }

    public void SetScale(Vector2 scale)
    {
        PersistentWrite(() =>
        {
            ValidateScale(scale);
            View.Scale = scale;
        });
    }

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
            var nextLayers = new HashSet<string>(targetLayers, StringComparer.Ordinal);
            resolver.Apply(nextLayers, appearance);
            var canonicalAppearance = resolver.Canonicalize(nextLayers);
            var epoch = AppearanceEpoch;
            targetLayers.Clear(); targetLayers.UnionWith(nextLayers);
            TargetAppearance = canonicalAppearance;
            var operation = GodotActorAppearanceTransition.CreateOperation(View, animation, easing,
            token => GodotActorAppearanceTransition.PrepareAsync(View,
                CreateComposite(activeLayers), CreateComposite(nextLayers), canvasBounds, Opacity,
                visible => { if (IsAlive) layerComposite.Visible = visible; },
                overlay =>
                {
                    activeOverlay = overlay;
                    if (overlay is not null) overlay.Modulate = new Color(1, 1, 1, Opacity);
                }, driver, animation, token),
            () =>
            {
                if (!IsAlive || AppearanceEpoch != epoch) return;
                activeLayers.Clear(); activeLayers.UnionWith(nextLayers);
                ApplyLayerVisibility(activeLayers);
                Appearance = canonicalAppearance;
            });
            return StartSequencedOperation(AppearanceProperty, operation, animation.ConflictPolicy);
        });
    }

    public Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(string appearance,
        AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        var sourceLayers = new HashSet<string>(targetLayers, StringComparer.Ordinal);
        var nextLayers = new HashSet<string>(targetLayers, StringComparer.Ordinal);
        resolver.Apply(nextLayers, appearance);
        var canonical = resolver.Canonicalize(nextLayers);
        return GodotActorAppearanceTransition.PrepareForCommitAsync(View,
            CreateComposite(sourceLayers), CreateComposite(nextLayers), canvasBounds,
            visible => { if (IsAlive) layerComposite.Visible = visible; },
            overlay =>
            {
                activeOverlay = overlay;
                if (overlay is not null) overlay.Modulate = new Color(1, 1, 1, Opacity);
            },
            () => { targetLayers.Clear(); targetLayers.UnionWith(nextLayers); TargetAppearance = canonical; },
            () => SetAppearance(canonical), driver, animation, cancellationToken);
    }

    private CanvasGroup CreateComposite(HashSet<string> layers)
    {
        var composite = (CanvasGroup)layerComposite.Duplicate();
        composite.Visible = true;
        composite.SetInstanceShaderParameter(GroupOpacityParameter, 1f);
        foreach (var sprite in composite.GetChildren().OfType<Sprite2D>())
        {
            sprite.Visible = nodeNameToLayerPath.TryGetValue(sprite.Name, out var path) && layers.Contains(path);
        }
        return composite;
    }

    public override void SetAppearance(string appearance)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            InvalidateAppearance();
            CancelPropertyAnimation(AppearanceProperty);
            resolver.Apply(activeLayers, appearance);
            targetLayers.Clear();
            targetLayers.UnionWith(activeLayers);
            ApplyLayerVisibility(activeLayers);
            ResetLayerOpacities();
            Appearance = resolver.Canonicalize(activeLayers);
            TargetAppearance = Appearance;
        });
    }

    public override Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preparedLayers = new HashSet<string>(targetLayers, StringComparer.Ordinal);
        resolver.Apply(preparedLayers, appearance);
        return Task.CompletedTask;
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
        return PersistentWrite(() =>
        {
            PositionPreset = preset;
            return ChangePosition(positions.Get(preset), animation);
        });
    }

    protected override void ApplyTexture(Texture2D texture)
    {
        // Layered actors keep the public Sprite2D as a transform/visibility host.
    }

    protected override void ApplyPosition(Vector2 position)
    {
        View.Position = position;
    }

    internal async Task BuildLayerViewsAsync(
        LayeredCharacterManifest manifest,
        IReadOnlyDictionary<string, Texture2D> atlases,
        IFrameClock? frameClock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(atlases);
        cancellationToken.ThrowIfCancellationRequested();
        var pixelsPerUnit = (float)manifest.Canvas.PixelsPerUnit;
        var frameStartedAt = Time.GetTicksUsec();
        foreach (var layer in manifest.Layers.OrderBy(layer => layer.SortingOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!atlases.TryGetValue(layer.Atlas, out var atlas))
            {
                throw new ActorException(
                    $"Layered character atlas '{layer.Atlas}' was not loaded.");
            }

            AddLayerView(
                layer,
                atlas,
                manifest,
                pixelsPerUnit,
                multiplyMaterial);
            if (Time.GetTicksUsec() - frameStartedAt < LayerBuildBudgetMicroseconds)
            {
                continue;
            }

            if (frameClock is not null)
            {
                await frameClock.NextFrameAsync(cancellationToken);
            }

            frameStartedAt = Time.GetTicksUsec();
        }

        ApplyLayerVisibility(activeLayers);
        canvasBounds = CalculateCanvasBounds();
    }

    private void BuildLayerViews(
        LayeredCharacterManifest manifest,
        string manifestPath,
        IReadOnlyDictionary<string, Texture2D>? preloadedAtlases)
    {
        var manifestDirectory = GetDirectory(manifestPath);
        var atlases = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        if (preloadedAtlases is not null)
        {
            foreach (var atlas in preloadedAtlases)
            {
                atlases.Add(atlas.Key, atlas.Value);
            }
        }

        var pixelsPerUnit = (float)manifest.Canvas.PixelsPerUnit;
        foreach (var layer in manifest.Layers.OrderBy(layer => layer.SortingOrder))
        {
            if (!atlases.TryGetValue(layer.Atlas, out var atlas))
            {
                atlas = TextureLoader.Load($"{manifestDirectory}/{layer.Atlas}");
                atlases.Add(layer.Atlas, atlas);
            }

            AddLayerView(
                layer,
                atlas,
                manifest,
                pixelsPerUnit,
                multiplyMaterial);
        }

        ApplyLayerVisibility(activeLayers);
        canvasBounds = CalculateCanvasBounds();
    }

    private void AddLayerView(
        LayeredCharacterLayer layer,
        Texture2D atlas,
        LayeredCharacterManifest manifest,
        float pixelsPerUnit,
        ShaderMaterial multiplyMaterial)
    {
        var texture = new AtlasTexture
        {
            Atlas = atlas,
            Region = new Rect2(
                layer.Region.X,
                layer.Region.Y,
                layer.Region.Width,
                layer.Region.Height),
        };
        var layerView = new Sprite2D
        {
            Name = SanitizeNodeName(layer.Path),
            Texture = texture,
            Centered = true,
            Visible = activeLayers.Contains(layer.Path),
            Position = new Vector2(
                (float)((layer.Position.X - manifest.Canvas.OffsetX) * pixelsPerUnit),
                (float)(-(layer.Position.Y - manifest.Canvas.OffsetY) * pixelsPerUnit)),
            RotationDegrees = (float)-layer.RotationDegrees,
            Scale = new Vector2(
                (float)layer.Scale.X * (layer.FlipX ? -1 : 1),
                (float)layer.Scale.Y * (layer.FlipY ? -1 : 1)),
            Offset = new Vector2(
                (float)((0.5 - layer.Pivot.X) * layer.Region.Width),
                (float)((layer.Pivot.Y - 0.5) * layer.Region.Height)),
            // Children are inserted in SortingOrder order. Keep them in the
            // CanvasGroup's Z batch so the group can flatten every layer before
            // applying actor-wide opacity.
            ZIndex = 0,
            Modulate = new Color(
                (float)layer.Color.R,
                (float)layer.Color.G,
                (float)layer.Color.B,
                (float)layer.Color.A),
        };
        if (layer.Material.Contains("Multiply", StringComparison.OrdinalIgnoreCase))
        {
            layerView.Material = multiplyMaterial;
        }

        layerComposite.AddChild(layerView);
        layerViews.Add(layer.Path, layerView);
        nodeNameToLayerPath[layerView.Name] = layer.Path;
    }

    private void ApplyLayerVisibility(HashSet<string> visibleLayers)
    {
        foreach (var layer in layerViews)
        {
            layer.Value.Visible = visibleLayers.Contains(layer.Key);
        }
    }

    private Rect2 CalculateCanvasBounds()
    {
        Rect2? bounds = null;
        foreach (var layer in layerViews.Values)
        {
            var localRect = layer.GetRect();
            var xform = layer.Transform;
            var p0 = xform * localRect.Position;
            var p1 = xform * new Vector2(localRect.Position.X + localRect.Size.X, localRect.Position.Y);
            var p2 = xform * new Vector2(localRect.Position.X, localRect.Position.Y + localRect.Size.Y);
            var p3 = xform * (localRect.Position + localRect.Size);

            var minX = Mathf.Min(Mathf.Min(p0.X, p1.X), Mathf.Min(p2.X, p3.X));
            var maxX = Mathf.Max(Mathf.Max(p0.X, p1.X), Mathf.Max(p2.X, p3.X));
            var minY = Mathf.Min(Mathf.Min(p0.Y, p1.Y), Mathf.Min(p2.Y, p3.Y));
            var maxY = Mathf.Max(Mathf.Max(p0.Y, p1.Y), Mathf.Max(p2.Y, p3.Y));

            var rect = new Rect2(minX, minY, maxX - minX, maxY - minY);
            bounds = bounds.HasValue ? bounds.Value.Merge(rect) : rect;
        }

        if (!bounds.HasValue || bounds.Value.Size.X <= 0 || bounds.Value.Size.Y <= 0)
        {
            return new Rect2(0, 0, 1, 1);
        }

        return bounds.Value;
    }

    private void SetCompositeOpacity(float opacity)

    {
        layerComposite.SetInstanceShaderParameter(GroupOpacityParameter, opacity);
        layerComposite.QueueRedraw();
    }

    private void ResetLayerOpacities()
    {
        foreach (var layer in layerViews.Values)
        {
            SetLayerOpacity(layer, 1);
        }
    }

    private static void SetLayerOpacity(CanvasItem layer, float opacity)
    {
        var color = layer.SelfModulate;
        layer.SelfModulate = new Color(color.R, color.G, color.B, opacity);
    }

    private static string GetDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash <= "res:/".Length)
        {
            throw new ActorException($"Layered character manifest path '{path}' is invalid.");
        }

        return path[..slash];
    }

    private static string SanitizeNodeName(string path) => path.Replace('/', '_');

    internal static ShaderMaterial CreateAlphaAwareMultiplyMaterial() => new()
    {
        Shader = ResourceLoader.Load<Shader>(AlphaAwareMultiplyShaderPath) ??
            throw new ActorException(
                $"Layered character shader could not be loaded: '{AlphaAwareMultiplyShaderPath}'."),
    };

    private static ShaderMaterial CreateLayeredCompositeMaterial() => new()
    {
        Shader = ResourceLoader.Load<Shader>(LayeredCompositeShaderPath) ??
            throw new ActorException(
                $"Layered composite shader could not be loaded: '{LayeredCompositeShaderPath}'."),
    };

    private static void ValidateScale(Vector2 scale)
    {
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) ||
            scale.X <= 0 || scale.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        }
    }

    public void Dispose()
    {
        if (activeOverlay is not null && GodotObject.IsInstanceValid(activeOverlay))
        {
            activeOverlay.QueueFree();
            activeOverlay = null;
        }
        layerComposite.Dispose();
    }
}

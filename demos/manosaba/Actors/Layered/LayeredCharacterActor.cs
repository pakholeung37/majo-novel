#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;

namespace Majo.Demos.Manosaba.Actors.Layered;

public sealed class LayeredCharacterActor : GodotCharacterActorBase, IDisposable
{
    private const string GroupOpacityParameter = "group_opacity";
    private const ulong LayerBuildBudgetMicroseconds = 2_000;
    internal const string AlphaAwareMultiplyShaderPath =
        "res://demos/manosaba/Actors/Layered/Shaders/AlphaAwareMultiply.gdshader";
    internal const string LayeredCompositeShaderPath =
        "res://demos/manosaba/Actors/Layered/Shaders/LayeredComposite.gdshader";
    private readonly TextureAppearanceSupport textureSupport;
    private readonly IActorAppearanceTransitionService transitions;
    private readonly LayerCompositionResolver resolver;
    private readonly LayeredCharacterMasking masking;
    private readonly HashSet<string> activeLayers;
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
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        LayeredCharacterManifest manifest,
        string manifestPath,
        CharacterPositionPreset initialPosition,
        ShaderMaterial? multiplyMaterial = null,
        bool buildLayerViews = true,
        IActorAppearanceTransitionService? transitions = null,
        string compositeShaderPath = LayeredCompositeShaderPath)
        : base(definition, view, positions, initialPosition, manifest.DefaultAppearance)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        textureSupport = new TextureAppearanceSupport(textureLoader, view);
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
        this.multiplyMaterial = multiplyMaterial ?? CreateAlphaAwareMultiplyMaterial();
        layerComposite = new CanvasGroup
        {
            Name = "LayerComposite",
            Material = CreateLayeredCompositeMaterial(compositeShaderPath),
        };
        View.AddChild(layerComposite);
        SetCompositeOpacity(1);
        resolver = new LayerCompositionResolver(manifest);
        masking = new LayeredCharacterMasking(manifest);
        activeLayers = resolver.CreateDefaultState();
        if (buildLayerViews)
        {
            BuildLayerViews(manifest, manifestPath, null);
            ApplyLayerVisibility(activeLayers);
        }
        Appearance = resolver.Canonicalize(activeLayers);
    }

    public new Sprite2D View => (Sprite2D)base.View;
    public override float Opacity =>
        (float)layerComposite.GetInstanceShaderParameter(GroupOpacityParameter);

    protected override void ApplyOpacity(float opacity)
    {
        SetCompositeOpacity(opacity);
        if (activeOverlay is not null && GodotObject.IsInstanceValid(activeOverlay))
        {
            activeOverlay.Modulate = new Color(1, 1, 1, opacity);
        }
    }

    public override IDisposable RetainVisualPreparation() => textureSupport.RetainVisualPreparation();

    public override string ResolveAppearance(string sourceAppearance, string appearance)
    {
        var layers = resolver.CreateDefaultState();
        resolver.Apply(layers, sourceAppearance);
        resolver.Apply(layers, appearance);
        return resolver.Canonicalize(layers);
    }

    public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance,
        string appearance,
        AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceLayers = resolver.CreateDefaultState();
        resolver.Apply(sourceLayers, sourceAppearance);
        var nextLayers = resolver.CreateDefaultState();
        var canonical = ResolveAppearance(sourceAppearance, appearance);
        resolver.Apply(nextLayers, canonical);
        using var timing = Majo.Runtime.Diagnostics.RuntimePerformanceTrace.Source.StartActivity("layered.appearance.setup.sync");
        timing?.SetTag("sameLayers", sourceLayers.SetEquals(nextLayers));
        timing?.SetTag("sourceAppearance", sourceAppearance);
        timing?.SetTag("targetAppearance", canonical);
        // The backend still owns the normal appearance slot and duration. Only
        // stable, equal built-in dissolves can dispense with visual inputs.
        // Custom preparation services keep their full lifecycle and side effects.
        if (transitions is GodotActorAppearanceTransitionService &&
            driver is DissolveTransitionDriver { PreservesIdenticalInputs: true } &&
            activeOverlay is null && activeLayers.SetEquals(sourceLayers) && sourceLayers.SetEquals(nextLayers))
        {
            timing?.SetTag("reusedStableAppearance", true);
            return Task.FromResult<IPreparedActorAppearance>(new UnchangedAppearance(this, canonical));
        }
        return transitions.PrepareForCommitAsync(View,
            CreateComposite(sourceLayers), CreateComposite(nextLayers), canvasBounds,
            visible => { if (IsAlive) layerComposite.Visible = visible; },
            overlay =>
            {
                activeOverlay = overlay;
                if (overlay is not null) overlay.Modulate = new Color(1, 1, 1, Opacity);
            },
            () => { },
            () => SetAppearance(canonical), canonical, driver, animation, cancellationToken);
    }

    private CanvasGroup CreateComposite(HashSet<string> layers)
    {
        using var timing = Majo.Runtime.Diagnostics.RuntimePerformanceTrace.Source.StartActivity("layered.clone.sync");
        timing?.SetTag("totalLayers", layerComposite.GetChildCount());
        timing?.SetTag("selectedLayers", layers.Count);
        // This actor owns a flat group of sprites. Preserve child order and copy
        // only selected layers, including their visible stencil writers. Masking
        // resolves writers from this endpoint, never from the other appearance.
        var composite = new CanvasGroup
        {
            Name = layerComposite.Name,
            Material = layerComposite.Material,
            Transform = layerComposite.Transform,
            Modulate = layerComposite.Modulate,
            SelfModulate = layerComposite.SelfModulate,
            VisibilityLayer = layerComposite.VisibilityLayer,
            FitMargin = layerComposite.FitMargin,
            ClearMargin = layerComposite.ClearMargin,
            UseMipmaps = layerComposite.UseMipmaps,
        };
        try
        {
            composite.SetInstanceShaderParameter(GroupOpacityParameter, 1f);
            foreach (var sprite in layerComposite.GetChildren().OfType<Sprite2D>())
            {
                if (!nodeNameToLayerPath.TryGetValue(sprite.Name, out var path) || !layers.Contains(path)) continue;
                var copy = (Sprite2D)sprite.Duplicate();
                copy.Visible = true;
                composite.AddChild(copy);
            }
            masking.Apply(composite);
            timing?.SetTag("copiedLayers", composite.GetChildCount());
            return composite;
        }
        catch { composite.Free(); throw; }
    }

    private sealed class UnchangedAppearance(LayeredCharacterActor actor, string appearance) : IPreparedActorAppearance
    {
        private bool committed;
        private bool disposed;
        public string ResolvedAppearance { get; } = appearance;
        public void Commit()
        {
            if (disposed || committed) throw new InvalidOperationException("Appearance is no longer preparable.");
            committed = true;
        }
        public void ApplyProgress(float progress) { }
        public void Finish()
        {
            if (disposed) return;
            disposed = true;
            if (committed) actor.SetAppearance(ResolvedAppearance);
        }
        public void Dispose() => disposed = true;
    }

    public override void SetAppearance(string appearance)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            InvalidateAppearance();
            resolver.Apply(activeLayers, appearance);
            ApplyLayerVisibility(activeLayers);
            ResetLayerOpacities();
            Appearance = resolver.Canonicalize(activeLayers);
        });
    }

    public override Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preparedLayers = new HashSet<string>(activeLayers, StringComparer.Ordinal);
        resolver.Apply(preparedLayers, appearance);
        return Task.CompletedTask;
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

    internal async Task WarmAppearanceRenderingAsync(CancellationToken cancellationToken)
    {
        // Populate both retained capture pairs before publishing this actor. This
        // also exercises its existing layer materials without changing appearance.
        using var first = await transitions.PrepareSnapshotAsync(View,
            CreateComposite(activeLayers), CreateComposite(activeLayers), canvasBounds, 1,
            _ => { }, _ => { }, new DissolveTransitionDriver(),
            new AnimationSpec(.1), cancellationToken);
        using var second = await transitions.PrepareSnapshotAsync(View,
            CreateComposite(activeLayers), CreateComposite(activeLayers), canvasBounds, 1,
            _ => { }, _ => { }, new DissolveTransitionDriver(),
            new AnimationSpec(.1), cancellationToken);
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
                atlas = textureSupport.Loader.Load($"{manifestDirectory}/{layer.Atlas}");
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
        masking.Apply(layerComposite);
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

    internal static ShaderMaterial CreateAlphaAwareMultiplyMaterial(string path = AlphaAwareMultiplyShaderPath) => new()
    {
        Shader = ResourceLoader.Load<Shader>(path) ??
            throw new ActorException(
                $"Layered character shader could not be loaded: '{path}'."),
    };

    private static ShaderMaterial CreateLayeredCompositeMaterial(string path) => new()
    {
        Shader = ResourceLoader.Load<Shader>(path) ??
            throw new ActorException(
                $"Layered composite shader could not be loaded: '{path}'."),
    };

    public void Dispose()
    {
        if (activeOverlay is not null && GodotObject.IsInstanceValid(activeOverlay))
        {
            activeOverlay.QueueFree();
            activeOverlay = null;
        }
        layerComposite.Dispose();
    }

    protected override void OnDestroy() => Dispose();
}

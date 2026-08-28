#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class LayeredCharacterActor : ActorBase, ICharacterActor
{
    private const ulong LayerBuildBudgetMicroseconds = 2_000;
    private const string AlphaAwareMultiplyShaderPath =
        "res://addons/majo_runtime/Runtime/Actors/Godot/Shaders/AlphaAwareMultiply.gdshader";
    private readonly CharacterPositionMap positions;
    private readonly LayerCompositionResolver resolver;
    private readonly HashSet<string> activeLayers;
    private readonly CanvasGroup layerComposite;
    private readonly ShaderMaterial multiplyMaterial;
    private readonly Dictionary<string, Sprite2D> layerViews =
        new(StringComparer.Ordinal);

    public LayeredCharacterActor(
        ActorDefinition definition,
        Sprite2D view,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        LayeredCharacterManifest manifest,
        string manifestPath,
        CharacterPositionPreset initialPosition,
        ShaderMaterial? multiplyMaterial = null,
        bool buildLayerViews = true)
        : base(
            definition,
            view,
            scheduler,
            textureLoader,
            manifest.DefaultAppearance)
    {
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        this.multiplyMaterial = multiplyMaterial ?? CreateAlphaAwareMultiplyMaterial();
        layerComposite = new CanvasGroup
        {
            Name = "LayerComposite",
        };
        View.AddChild(layerComposite);
        resolver = new LayerCompositionResolver(manifest);
        activeLayers = resolver.CreateDefaultState();
        if (buildLayerViews)
        {
            BuildLayerViews(manifest, manifestPath, null);
            ApplyLayerVisibility();
        }
        Appearance = resolver.Canonicalize(activeLayers);
        TargetAppearance = Appearance;
        PositionPreset = initialPosition;
        ApplyPosition(positions.Get(PositionPreset));
        TargetPosition = Position;
    }

    public new Sprite2D View => (Sprite2D)base.View;

    public override Vector2 Position => View.Position;

    public Vector2 Scale => View.Scale;

    public CharacterPositionPreset PositionPreset { get; private set; }

    public void SetScale(Vector2 scale)
    {
        ValidateScale(scale);
        View.Scale = scale;
    }

    public IOperationHandle ChangeAppearance(
        string appearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null)
    {
        var nextLayers = new HashSet<string>(activeLayers, StringComparer.Ordinal);
        resolver.Apply(nextLayers, appearance);
        var canonicalAppearance = resolver.Canonicalize(nextLayers);
        return CrossFadeToAppearance(
            canonicalAppearance,
            () =>
            {
                activeLayers.Clear();
                activeLayers.UnionWith(nextLayers);
                ApplyLayerVisibility();
            },
            durationSeconds,
            operationId,
            groupId,
            layerComposite);
    }

    public IOperationHandle ChangeScenePosition(
        Vector2 scenePosition,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => ChangePosition(
            positions.GetScenePosition(scenePosition),
            durationSeconds,
            operationId,
            groupId);

    public override void SetAppearance(string appearance)
    {
        resolver.Apply(activeLayers, appearance);
        ApplyLayerVisibility();
        Appearance = resolver.Canonicalize(activeLayers);
        TargetAppearance = Appearance;
    }

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
        return ChangePosition(positions.Get(preset), animation, operationId, groupId);
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

            AddLayerView(layer, atlas, manifest, pixelsPerUnit, multiplyMaterial);
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

        ApplyLayerVisibility();
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

            AddLayerView(layer, atlas, manifest, pixelsPerUnit, multiplyMaterial);
        }
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
            ZIndex = layer.SortingOrder,
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
    }

    private void ApplyLayerVisibility()
    {
        foreach (var layer in layerViews)
        {
            layer.Value.Visible = activeLayers.Contains(layer.Key);
        }
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

    private static void ValidateScale(Vector2 scale)
    {
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) ||
            scale.X <= 0 || scale.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        }
    }
}

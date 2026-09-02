#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class LayeredCharacterActor : ActorBase, ICharacterActor, IDisposable
{
    private const string AppearanceProperty = "layered-appearance";
    private const string GroupOpacityParameter = "group_opacity";
    private const ulong LayerBuildBudgetMicroseconds = 2_000;
    private const string AlphaAwareMultiplyShaderPath =
        "res://addons/majo_runtime/Runtime/Actors/Godot/Shaders/AlphaAwareMultiply.gdshader";
    private const string LayeredCompositeShaderPath =
        "res://addons/majo_runtime/Runtime/Actors/Godot/Shaders/LayeredComposite.gdshader";
    private readonly CharacterPositionMap positions;
    private readonly LayerCompositionResolver resolver;
    private readonly HashSet<string> activeLayers;
    private readonly HashSet<string> targetLayers;
    private readonly ShaderMaterial multiplyMaterial;
    private readonly CanvasGroup layerComposite;
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

    public override Vector2 Position => View.Position;

    public override float Opacity =>
        (float)layerComposite.GetInstanceShaderParameter(GroupOpacityParameter);

    public Vector2 Scale => View.Scale;

    public CharacterPositionPreset PositionPreset { get; private set; }

    public void SetScenePosition(Vector2 scenePosition) =>
        SetPosition(positions.GetScenePosition(scenePosition));

    public void SetPosition(CharacterPositionPreset preset)
    {
        PositionPreset = preset;
        SetPosition(positions.Get(preset));
    }

    public override void SetOpacity(float opacity)
    {
        ValidateOpacity(opacity);
        CancelPropertyAnimation(OpacityProperty);
        SetCompositeOpacity(opacity);
        TargetOpacity = opacity;
    }

    public override IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => ChangeOpacity(
        opacity,
        new AnimationSpec(durationSeconds),
        operationId,
        groupId);

    public override IOperationHandle ChangeOpacity(
        float opacity,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        ValidateOpacity(opacity);
        TargetOpacity = opacity;
        return StartSequencedOperation(
            OpacityProperty,
            CreateCompositeOpacityTween(opacity, animation),
            operationId,
            groupId,
            animation.ConflictPolicy);
    }

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
        var nextLayers = new HashSet<string>(targetLayers, StringComparer.Ordinal);
        resolver.Apply(nextLayers, appearance);
        var canonicalAppearance = resolver.Canonicalize(nextLayers);
        targetLayers.Clear();
        targetLayers.UnionWith(nextLayers);
        TargetAppearance = canonicalAppearance;
        TargetVisible = true;
        TargetOpacity = 1;
        var animation = new AnimationSpec(durationSeconds);
        OperationDefinition[] fades = [];
        string[] exitingLayers = [];
        string[] enteringLayers = [];
        var prepared = false;
        var committed = false;

        void PrepareTransition()
        {
            if (prepared)
            {
                return;
            }

            exitingLayers = activeLayers.Except(nextLayers, StringComparer.Ordinal).ToArray();
            enteringLayers = nextLayers.Except(activeLayers, StringComparer.Ordinal).ToArray();
            var transitionFades = new List<OperationDefinition>(
                exitingLayers.Length + enteringLayers.Length);
            foreach (var layerPath in exitingLayers)
            {
                var layer = layerViews[layerPath];
                layer.Visible = true;
                SetLayerOpacity(layer, 1);
                transitionFades.Add(CreateLayerOpacityTween(layer, 0, animation));
            }

            foreach (var layerPath in enteringLayers)
            {
                var layer = layerViews[layerPath];
                layer.Visible = true;
                SetLayerOpacity(layer, 0);
                transitionFades.Add(CreateLayerOpacityTween(layer, 1, animation));
            }

            fades = transitionFades.ToArray();
            View.Visible = true;
            prepared = true;
        }

        void Commit()
        {
            if (committed)
            {
                return;
            }

            PrepareTransition();
            foreach (var layerPath in exitingLayers)
            {
                var layer = layerViews[layerPath];
                layer.Visible = false;
                SetLayerOpacity(layer, 1);
            }

            foreach (var layerPath in enteringLayers)
            {
                var layer = layerViews[layerPath];
                layer.Visible = true;
                SetLayerOpacity(layer, 1);
            }

            activeLayers.Clear();
            activeLayers.UnionWith(nextLayers);
            Appearance = canonicalAppearance;
            committed = true;
        }

        var transition = new OperationDefinition(
            async context =>
            {
                PrepareTransition();
                await Task.WhenAll(fades.Select(fade => fade.ExecuteAsync(context)));
                Commit();
            },
            completeAsync: async cancellationToken =>
            {
                PrepareTransition();
                await Task.WhenAll(fades.Select(
                    fade => fade.CompleteAsync(cancellationToken)));
                Commit();
            },
            cancelAsync: async cancellationToken =>
            {
                PrepareTransition();
                await Task.WhenAll(fades.Select(
                    fade => fade.CancelAsync(cancellationToken)));
                Commit();
            },
            isSkippable: true);
        return StartSequencedOperation(
            AppearanceProperty,
            transition,
            operationId,
            groupId);
    }

    public override void SetAppearance(string appearance)
    {
        CancelPropertyAnimation(AppearanceProperty);
        resolver.Apply(activeLayers, appearance);
        targetLayers.Clear();
        targetLayers.UnionWith(activeLayers);
        ApplyLayerVisibility(activeLayers);
        ResetLayerOpacities();
        Appearance = resolver.Canonicalize(activeLayers);
        TargetAppearance = Appearance;
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
    }

    private void ApplyLayerVisibility(HashSet<string> visibleLayers)
    {
        foreach (var layer in layerViews)
        {
            layer.Value.Visible = visibleLayers.Contains(layer.Key);
        }
    }

    private OperationDefinition CreateLayerOpacityTween(
        CanvasItem target,
        float opacity,
        AnimationSpec animation) =>
        GodotTweenOperation.CreatePropertyTween(
            View,
            target,
            new NodePath("self_modulate:a"),
            opacity,
            animation.DurationSeconds,
            transition: GodotAnimationSpec.Transition(animation.Transition),
            ease: GodotAnimationSpec.Ease(animation.Ease),
            ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);

    private OperationDefinition CreateCompositeOpacityTween(
        float opacity,
        AnimationSpec animation) =>
        GodotTweenOperation.CreateFloatMethodTween(
            View,
            () => Opacity,
            SetCompositeOpacity,
            opacity,
            animation.DurationSeconds,
            transition: GodotAnimationSpec.Transition(animation.Transition),
            ease: GodotAnimationSpec.Ease(animation.Ease),
            ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);

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

    public void Dispose() => layerComposite.Dispose();
}

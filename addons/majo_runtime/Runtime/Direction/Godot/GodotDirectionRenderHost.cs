#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionRenderHost : IRuntimeService
{
    private static readonly Vector2I FallbackResolution = new(1280, 720);
    private readonly Node owner;
    private readonly Vector2I configuredResolution;
    private readonly int maxStages;
    private readonly Dictionary<StageId, GodotDirectionStageSurface> stages = new();
    private readonly Dictionary<ViewId, GodotDirectionViewSurface> views = new();
    private readonly Dictionary<LayoutSlotId, TextureRect> screenSlots = new();
    private readonly Dictionary<ViewId, long> viewGenerations = new();
    private readonly Dictionary<StageId, long> stageGenerations = new();
    private long screenGeneration;
    private long effectGeneration;
    private ScreenState? screenLayoutBefore;
    private ScreenState? screenLayoutTarget;
    private float screenLayoutProgress;
    private BackdropState? transitionBackdrop;
    private Viewport? ownerViewport;
    private bool isInitialized;
    private bool shutdownRequested;

    public GodotDirectionRenderHost(
        Node owner,
        Vector2I referenceResolution,
        int maxStages = 8)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        configuredResolution = referenceResolution;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStages);
        this.maxStages = maxStages;

        StageViewport = CreateViewport("MajoDirectionStageViewport");
        StageCanvas = new Control
        {
            Name = "StageCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StageBackground = new ColorRect
        {
            Name = "StageBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Backdrop = CreateTextureRect("Backdrop");
        BackdropTransition = CreateTextureRect("BackdropTransition");
        BackdropTransition.Visible = false;
        Actors = new Node2D { Name = "Actors" };
        StageCanvas.AddChild(StageBackground);
        StageCanvas.AddChild(Backdrop);
        StageCanvas.AddChild(BackdropTransition);
        StageCanvas.AddChild(Actors);
        StageViewport.AddChild(StageCanvas);
        stages.Add(StageId.Main, new GodotDirectionStageSurface(
            StageId.Main,
            StageViewport,
            StageCanvas,
            StageBackground,
            Backdrop,
            BackdropTransition,
            Actors));

        ViewViewport = CreateViewport("MajoDirectionViewViewport");
        ViewCanvas = new Control
        {
            Name = "ViewCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ViewTransform = new Node2D { Name = "ViewTransform" };
        ViewContent = CreateTextureRect("ViewContent");
        var viewTransition = CreateTextureRect("ViewTransition");
        viewTransition.Visible = false;
        ViewTransform.AddChild(ViewContent);
        ViewCanvas.AddChild(ViewTransform);
        ViewCanvas.AddChild(viewTransition);
        ViewViewport.AddChild(ViewCanvas);
        views.Add(ViewId.Main, new GodotDirectionViewSurface(
            ViewId.Main,
            ViewViewport,
            ViewCanvas,
            ViewTransform,
            ViewContent,
            viewTransition));

        ScreenViewport = CreateViewport("MajoDirectionScreenViewport");
        ScreenCanvas = new Control
        {
            Name = "ScreenCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var mainScreenSlot = CreateTextureRect("ScreenSlot-main");
        ScreenTransitionSource = CreateTextureRect("ScreenTransitionSource");
        ScreenTransitionSource.ZIndex = 100;
        ScreenTransitionSource.Visible = false;
        ScreenCanvas.AddChild(mainScreenSlot);
        ScreenCanvas.AddChild(ScreenTransitionSource);
        ScreenViewport.AddChild(ScreenCanvas);
        screenSlots[LayoutSlotId.Main] = mainScreenSlot;

        OutputLayer = new CanvasLayer
        {
            Name = "MajoDirectionScreen",
            Layer = 5,
            Visible = false,
        };
        LiveOutput = CreateTextureRect("LiveOutput");
        Curtain = new ColorRect
        {
            Name = "DirectionCurtain",
            Color = new Color(Colors.Black, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        OutputLayer.AddChild(LiveOutput);
        OutputLayer.AddChild(Curtain);
    }

    public SubViewport StageViewport { get; }
    public Control StageCanvas { get; }
    public ColorRect StageBackground { get; }
    public TextureRect Backdrop { get; }
    public TextureRect BackdropTransition { get; }
    public Node2D Actors { get; }
    public SubViewport ViewViewport { get; }
    public Control ViewCanvas { get; }
    public Node2D ViewTransform { get; }
    public TextureRect ViewContent { get; }
    public SubViewport ScreenViewport { get; }
    public Control ScreenCanvas { get; }
    public CanvasLayer OutputLayer { get; }
    public TextureRect LiveOutput { get; }
    public TextureRect ScreenTransitionSource { get; }
    public ColorRect Curtain { get; }
    public BackdropState? PublishedBackdrop { get; private set; }
    public ViewState? PublishedView { get; private set; }
    public ScreenState PublishedScreen { get; private set; } = ScreenState.Default;
    public StableEffectState PublishedEffects { get; private set; } = StableEffectState.Default;

    public IReadOnlyCollection<StageId> StageIds => stages.Keys;
    public IReadOnlyCollection<ViewId> ViewIds => views.Keys;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdownRequested)
            throw new DirectionException("Godot Direction render host cannot be reinitialized after shutdown.");
        if (isInitialized) return;

        owner.AddChild(StageViewport);
        owner.AddChild(ViewViewport);
        owner.AddChild(ScreenViewport);
        owner.AddChild(OutputLayer);
        Resize();
        ViewContent.Texture = StageViewport.GetTexture();
        screenSlots[LayoutSlotId.Main].Texture = ViewViewport.GetTexture();
        LiveOutput.Texture = ScreenViewport.GetTexture();
        isInitialized = true;
        PublishedView = new ViewState(
            ViewId.Main,
            StageId.Main,
            ViewPose.Default);
        ApplyViewPose(ViewId.Main, ViewPose.Default);
        PublishEffects(StableEffectState.Default);
        ownerViewport = owner.GetViewport();
        ownerViewport.SizeChanged += Resize;
        viewGenerations[ViewId.Main] = 0;
        stageGenerations[StageId.Main] = 0;
        ActivateViewports();
        await WaitForRenderFenceAsync(cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        InvalidateGenerations();
        foreach (var surface in stages.Values.ToArray())
        {
            surface.Backdrop.Texture = null;
            surface.Backdrop.Modulate = Colors.White;
            surface.Backdrop.Visible = false;
            ClearBackdropTransition(surface.Id);
            surface.PublishedBackdrop = null;
            if (surface.Id != StageId.Main)
            {
                stages.Remove(surface.Id);
                QueueFree(surface.Viewport);
            }
        }
        foreach (var view in views.Values.ToArray())
        {
            if (view.Id == ViewId.Main) continue;
            QueueFree(view.Viewport);
            views.Remove(view.Id);
        }
        foreach (var slot in screenSlots.Keys.Where(slot => slot != LayoutSlotId.Main).ToArray())
        {
            QueueFree(screenSlots[slot]);
            screenSlots.Remove(slot);
        }
        ViewContent.Texture = StageViewport.GetTexture();
        ViewContent.Modulate = Colors.White;
        ClearViewTransition(ViewId.Main);
        ClearScreenTransition();
        PublishedBackdrop = null;
        PublishedView = new ViewState(
            ViewId.Main,
            StageId.Main,
            ViewPose.Default);
        ApplyViewPose(ViewId.Main, ViewPose.Default);
        PublishedScreen = ScreenState.Default;
        PublishScreen(ScreenState.Default);
        PublishEffects(StableEffectState.Default);
        Deactivate();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return Task.CompletedTask;
        InvalidateGenerations();
        if (ownerViewport is not null && GodotObject.IsInstanceValid(ownerViewport))
            ownerViewport.SizeChanged -= Resize;
        ClearBackdropTransition(StageId.Main);
        ClearViewTransition(ViewId.Main);
        ClearScreenTransition();
        QueueFree(OutputLayer);
        QueueFree(ScreenViewport);
        QueueFree(ViewViewport);
        foreach (var surface in stages.Values)
            QueueFree(surface.Viewport);
        stages.Clear();
        foreach (var view in views.Values)
        {
            if (view.Id != ViewId.Main && GodotObject.IsInstanceValid(view.Viewport))
                QueueFree(view.Viewport);
        }
        views.Clear();
        screenSlots.Clear();
        PublishedBackdrop = null;
        PublishedView = null;
        PublishedScreen = ScreenState.Default;
        PublishedEffects = StableEffectState.Default;
        ownerViewport = null;
        isInitialized = false;
        shutdownRequested = true;
        return Task.CompletedTask;
    }

    public void PublishBackdrop(StageId stageId, BackdropState? state, Texture2D? texture)
    {
        EnsureInitialized();
        Activate();
        var surface = GetStage(stageId);
        surface.PublishedBackdrop = state;
        if (stageId == StageId.Main) PublishedBackdrop = state;
        surface.Backdrop.Texture = texture;
        surface.Backdrop.Modulate = state is null
            ? Colors.White
            : ToGodot(state.Tint);
        surface.Backdrop.Visible = state is not null && texture is not null;
        if (state is not null && texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, state.Fit);
    }

    public void PublishBackdropTransition(
        StageId stageId,
        BackdropState state,
        Texture2D texture)
    {
        EnsureInitialized();
        var surface = GetStage(stageId);
        surface.TransitionBackdrop = state ?? throw new ArgumentNullException(nameof(state));
        if (stageId == StageId.Main) transitionBackdrop = state;
        surface.BackdropTransition.Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        surface.BackdropTransition.Modulate = ToGodot(state.Tint);
        surface.BackdropTransition.Visible = true;
        LayoutBackdrop(surface, surface.BackdropTransition, state.Fit);
    }

    public void PublishView(ViewId viewId, ViewState state, Texture2D texture)
    {
        EnsureInitialized();
        Activate();
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texture);
        var surface = GetOrCreateView(viewId);
        surface.Content.Texture = texture;
        surface.Content.Modulate = Colors.White;
        surface.PublishedView = state;
        ApplyViewPose(viewId, state.Pose);
        if (viewId == ViewId.Main) PublishedView = state;
    }

    internal long BeginViewGeneration(ViewId viewId) => ++viewGenerations[viewId];
    internal bool IsCurrentViewGeneration(ViewId viewId, long generation) =>
        viewGenerations.TryGetValue(viewId, out var current) && current == generation;
    internal long BeginStageGeneration(StageId stageId)
    {
        stageGenerations.TryGetValue(stageId, out var current);
        stageGenerations[stageId] = current + 1;
        return current + 1;
    }
    internal bool IsCurrentStageGeneration(StageId stageId, long generation) =>
        stageGenerations.TryGetValue(stageId, out var current) && current == generation;
    internal long BeginScreenGeneration() => ++screenGeneration;
    internal bool IsCurrentScreenGeneration(long generation) => screenGeneration == generation;
    internal long BeginEffectGeneration() => ++effectGeneration;
    internal bool IsCurrentEffectGeneration(long generation) => effectGeneration == generation;
    private void InvalidateGenerations()
    {
        screenGeneration++;
        effectGeneration++;
        foreach (var id in viewGenerations.Keys.ToArray()) viewGenerations[id]++;
        foreach (var id in stageGenerations.Keys.ToArray()) stageGenerations[id]++;
    }
    internal TextureRect GetViewTransitionSource(ViewId viewId) => GetOrCreateView(viewId).TransitionSource;
    internal ViewPose GetCurrentViewPose(ViewId viewId) => GetOrCreateView(viewId).CurrentPose;
    internal async Task WaitForRenderFenceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.GetTree() is SceneTree tree)
            await owner.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void PublishScreen(ScreenState state)
    {
        EnsureInitialized();
        Activate();
        PublishedScreen = state ?? throw new ArgumentNullException(nameof(state));
        screenLayoutBefore = null;
        screenLayoutTarget = null;
        foreach (var slot in screenSlots.Values) slot.Visible = false;
        foreach (var pair in state.Bindings)
        {
            var output = GetOrCreateScreenSlot(pair.Key);
            var view = RequireView(pair.Value);
            output.Texture = view.Viewport.GetTexture();
            output.Visible = true;
            LayoutSlot(output, state.Layout, pair.Key, ToSize(owner.GetViewport().GetVisibleRect().Size));
        }
    }

    internal void PublishScreenLayout(ScreenState state, ScreenState before, float progress)
    {
        EnsureInitialized();
        PublishedScreen = state;
        screenLayoutBefore = before;
        screenLayoutTarget = state;
        screenLayoutProgress = Mathf.Clamp(progress, 0, 1);
        foreach (var slot in screenSlots.Values) slot.Visible = false;
        foreach (var slot in before.Bindings.Keys.Union(state.Bindings.Keys))
        {
            if (!state.Bindings.TryGetValue(slot, out var viewId) &&
                !before.Bindings.TryGetValue(slot, out viewId))
                continue;
            var output = GetOrCreateScreenSlot(slot);
            var view = RequireView(viewId);
            output.Texture = view.Viewport.GetTexture();
            var hasTarget = state.Bindings.ContainsKey(slot);
            output.Visible = hasTarget || progress < 1;
            var from = before.Bindings.ContainsKey(slot)
                ? SlotRect(before.Layout, slot, ToSize(owner.GetViewport().GetVisibleRect().Size))
                : SlotRect(state.Layout, slot, ToSize(owner.GetViewport().GetVisibleRect().Size));
            var to = hasTarget
                ? SlotRect(state.Layout, slot, ToSize(owner.GetViewport().GetVisibleRect().Size))
                : new Rect2(from.Position, Vector2.Zero);
            var rect = new Rect2(
                from.Position.Lerp(to.Position, progress),
                from.Size.Lerp(to.Size, progress));
            output.Position = rect.Position;
            output.Size = rect.Size;
        }
    }

    public void PublishEffects(StableEffectState state)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(state);
        state.Curtain.Validate();
        PublishedEffects = state;
        Curtain.Color = new Color(
            state.Curtain.Color.R,
            state.Curtain.Color.G,
            state.Curtain.Color.B,
            state.Curtain.Opacity);
        Curtain.Visible = state.Curtain.Opacity > 0;
    }

    public Tween CreateTween()
    {
        EnsureInitialized();
        return owner.CreateTween();
    }

    internal void ActivateOutput()
    {
        EnsureInitialized();
        Activate();
    }

    public ImageTexture CaptureView(ViewId viewId)
    {
        EnsureInitialized();
        var view = GetOrCreateView(viewId);
        Image image;
        if (!string.Equals(
            DisplayServer.GetName(),
            "headless",
            StringComparison.OrdinalIgnoreCase))
        {
            image = view.Viewport.GetTexture().GetImage();
            if (image is null || image.IsEmpty())
                throw new DirectionException("Direction View capture returned an empty image.");
        }
        else
        {
            var size = view.Viewport.Size;
            image = Image.CreateEmpty(
                Math.Max(size.X, 1),
                Math.Max(size.Y, 1),
                false,
                Image.Format.Rgba8);
            image.Fill(Colors.Black);
        }

        return ImageTexture.CreateFromImage(image);
    }

    internal async Task<ImageTexture> CaptureViewAsync(
        ViewId viewId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var view = GetOrCreateView(viewId);
        var previousMode = view.Viewport.RenderTargetUpdateMode;
        view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        try
        {
            await WaitForRenderFenceAsync(cancellationToken);
            return CaptureView(viewId);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(view.Viewport))
                view.Viewport.RenderTargetUpdateMode = previousMode;
        }
    }

    internal async Task<ImageTexture> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var previousMode = ScreenViewport.RenderTargetUpdateMode;
        ScreenViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        try
        {
            await WaitForRenderFenceAsync(cancellationToken);
            Image image;
            if (!string.Equals(
                DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase))
            {
                image = ScreenViewport.GetTexture().GetImage();
                if (image is null || image.IsEmpty())
                    throw new DirectionException("Direction Screen capture returned an empty image.");
            }
            else
            {
                image = Image.CreateEmpty(
                    Math.Max(ScreenViewport.Size.X, 1),
                    Math.Max(ScreenViewport.Size.Y, 1),
                    false,
                    Image.Format.Rgba8);
                image.Fill(Colors.Black);
            }
            return ImageTexture.CreateFromImage(image);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(ScreenViewport))
                ScreenViewport.RenderTargetUpdateMode = previousMode;
        }
    }

    public void ApplyViewPose(ViewId viewId, ViewPose pose)
    {
        EnsureInitialized();
        pose.Validate();
        var surface = GetOrCreateView(viewId);
        surface.CurrentPose = pose;
        ApplyViewTransform(surface);
    }

    internal void ApplyViewModifierDelta(ViewId viewId, CameraPoseDelta delta)
    {
        var surface = RequireView(viewId);
        surface.ModifierDelta = delta;
        ApplyViewTransform(surface);
    }

    internal void ClearViewModifierDelta(ViewId viewId)
    {
        if (!views.TryGetValue(viewId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.Transform)) return;
        surface.ModifierDelta = CameraPoseDelta.Identity;
        ApplyViewTransform(surface);
    }

    private static void ApplyViewTransform(GodotDirectionViewSurface surface)
    {
        var pose = surface.CurrentPose;
        var delta = surface.ModifierDelta;
        var effectiveRotation = pose.RotationDegrees + delta.RotationDegreesOffset;
        var effectiveZoom = pose.Zoom * delta.ZoomMultiplier;
        var size = new Vector2(surface.Viewport.Size.X, surface.Viewport.Size.Y);
        var basis = new Transform2D(
            Mathf.DegToRad(effectiveRotation),
            new Vector2(effectiveZoom, effectiveZoom),
            0,
            Vector2.Zero);
        var sceneCenter = new Vector2(
            size.X * pose.Center.X / 100,
            size.Y * (1 - pose.Center.Y / 100));
        surface.Transform.Transform = new Transform2D(
            basis.X,
            basis.Y,
            size / 2 - basis * sceneCenter + delta.CenterOffset);
    }

    public void ClearBackdropTransition(StageId stageId)
    {
        if (!stages.TryGetValue(stageId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.BackdropTransition)) return;
        surface.BackdropTransition.Visible = false;
        surface.BackdropTransition.Texture = null;
        surface.BackdropTransition.Modulate = Colors.White;
        surface.TransitionBackdrop = null;
        if (stageId == StageId.Main) transitionBackdrop = null;
    }

    public void ClearViewTransition(ViewId viewId)
    {
        if (!views.TryGetValue(viewId, out var view) ||
            !GodotObject.IsInstanceValid(view.TransitionSource)) return;
        view.TransitionSource.Visible = false;
        view.TransitionSource.Texture = null;
        view.TransitionSource.Modulate = Colors.White;
    }

    internal void PublishScreenTransition(Texture2D texture)
    {
        EnsureInitialized();
        ScreenTransitionSource.Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        ScreenTransitionSource.Modulate = Colors.White;
        ScreenTransitionSource.Visible = true;
    }

    internal void SetScreenTransitionOpacity(float opacity)
    {
        if (!GodotObject.IsInstanceValid(ScreenTransitionSource)) return;
        ScreenTransitionSource.Modulate = new Color(1, 1, 1, Mathf.Clamp(opacity, 0, 1));
    }

    internal void ClearScreenTransition()
    {
        if (!GodotObject.IsInstanceValid(ScreenTransitionSource)) return;
        ScreenTransitionSource.Visible = false;
        ScreenTransitionSource.Texture = null;
        ScreenTransitionSource.Modulate = Colors.White;
    }

    private void Resize()
    {
        var visibleSize = owner.GetViewport().GetVisibleRect().Size;
        var resolution = configuredResolution.X > 0 && configuredResolution.Y > 0
            ? configuredResolution
            : visibleSize.X > 0 && visibleSize.Y > 0
                ? new Vector2I(
                    Math.Max(1, Mathf.RoundToInt(visibleSize.X)),
                    Math.Max(1, Mathf.RoundToInt(visibleSize.Y)))
                : FallbackResolution;
        foreach (var surface in stages.Values) ResizeStage(surface, resolution);
        foreach (var view in views.Values)
        {
            view.Viewport.Size = resolution;
            SetFullRect(view.Canvas, resolution);
            SetFullRect(view.Content, resolution);
            SetFullRect(view.TransitionSource, resolution);
            if (view.PublishedView is not null)
                ApplyViewPose(view.Id, view.PublishedView.Pose);
        }
        var screenSize = visibleSize.X > 0 && visibleSize.Y > 0
            ? ToSize(visibleSize)
            : resolution;
        ScreenViewport.Size = screenSize;
        SetFullRect(ScreenCanvas, screenSize);
        SetFullRect(LiveOutput, screenSize);
        SetFullRect(ScreenTransitionSource, screenSize);
        SetFullRect(Curtain, screenSize);
        if (PublishedView is not null) ApplyViewPose(ViewId.Main, PublishedView.Pose);
        foreach (var pair in screenSlots)
            if (pair.Value.Visible)
                LayoutSlot(pair.Value, PublishedScreen.Layout, pair.Key, screenSize);
        if (screenLayoutBefore is not null && screenLayoutTarget is not null)
            PublishScreenLayout(screenLayoutTarget, screenLayoutBefore, screenLayoutProgress);
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
            throw new DirectionException("Godot Direction render host is not initialized.");
    }

    private void Activate()
    {
        ActivateViewports();
        OutputLayer.Visible = true;
    }

    private void ActivateViewports()
    {
        foreach (var surface in stages.Values)
            if (GodotObject.IsInstanceValid(surface.Viewport))
                surface.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        if (GodotObject.IsInstanceValid(ViewViewport))
            ViewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        if (GodotObject.IsInstanceValid(ScreenViewport))
            ScreenViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        foreach (var view in views.Values)
            if (GodotObject.IsInstanceValid(view.Viewport))
                view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }

    private void Deactivate()
    {
        OutputLayer.Visible = false;
        foreach (var surface in stages.Values)
            if (GodotObject.IsInstanceValid(surface.Viewport))
                surface.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        if (GodotObject.IsInstanceValid(ViewViewport))
            ViewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        if (GodotObject.IsInstanceValid(ScreenViewport))
            ScreenViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        foreach (var view in views.Values)
            if (GodotObject.IsInstanceValid(view.Viewport))
                view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private static SubViewport CreateViewport(string name) => new()
    {
        Name = name,
        Disable3D = true,
        TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        HandleInputLocally = false,
        GuiDisableInput = true,
    };

    private static TextureRect CreateTextureRect(string name) => new()
    {
        Name = name,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static void SetFullRect(Control control, Vector2I size)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = Vector2.Zero;
        control.Size = size;
    }

    private static Vector2I ToSize(Vector2 size) => new(
        Math.Max(1, Mathf.RoundToInt(size.X)),
        Math.Max(1, Mathf.RoundToInt(size.Y)));

    internal GodotDirectionStageSurface GetOrCreateStage(StageId stageId)
    {
        EnsureInitialized();
        if (stages.TryGetValue(stageId, out var existing)) return existing;
        if (stages.Count >= maxStages)
        {
            throw new DirectionException(
                $"Direction Stage limit of {maxStages} was exceeded while creating '{stageId}'.");
        }

        var viewport = CreateViewport($"MajoDirectionStageViewport-{stages.Count}");
        var canvas = new Control
        {
            Name = "StageCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var background = new ColorRect
        {
            Name = "StageBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var backdrop = CreateTextureRect("Backdrop");
        var transition = CreateTextureRect("BackdropTransition");
        transition.Visible = false;
        var actors = new Node2D { Name = "Actors" };
        canvas.AddChild(background);
        canvas.AddChild(backdrop);
        canvas.AddChild(transition);
        canvas.AddChild(actors);
        viewport.AddChild(canvas);
        var surface = new GodotDirectionStageSurface(
            stageId, viewport, canvas, background, backdrop, transition, actors);
        stages.Add(stageId, surface);
        owner.AddChild(viewport);
        ResizeStage(surface, StageViewport.Size);
        if (isInitialized)
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        return surface;
    }

    internal bool HasStage(StageId stageId) => stages.ContainsKey(stageId);

    internal void ReleasePreparedStage(StageId stageId)
    {
        if (stageId == StageId.Main || !stages.Remove(stageId, out var surface)) return;
        stageGenerations.Remove(stageId);
        if (GodotObject.IsInstanceValid(surface.Viewport))
        {
            surface.Viewport.GetParent()?.RemoveChild(surface.Viewport);
            surface.Viewport.QueueFree();
        }
    }

    internal GodotDirectionStageSurface GetStage(StageId stageId) =>
        stages.TryGetValue(stageId, out var surface)
            ? surface
            : throw new DirectionReferenceException(
                $"Direction Stage '{stageId}' has no prepared render surface.");

    internal GodotDirectionStageSurface RequireStage(StageId stageId) => GetStage(stageId);

    internal BackdropState? GetPublishedBackdrop(StageId stageId) =>
        GetStage(stageId).PublishedBackdrop;

    internal Texture2D GetStageTexture(StageId stageId) => GetStage(stageId).Viewport.GetTexture();

    internal GodotDirectionViewSurface GetOrCreateView(ViewId viewId)
    {
        EnsureInitialized();
        if (views.TryGetValue(viewId, out var existing)) return existing;
        var viewport = CreateViewport($"MajoDirectionViewViewport-{views.Count}");
        var canvas = new Control
        {
            Name = "ViewCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var transform = new Node2D { Name = "ViewTransform" };
        var content = CreateTextureRect("ViewContent");
        var viewTransition = CreateTextureRect("ViewTransition");
        viewTransition.Visible = false;
        transform.AddChild(content);
        canvas.AddChild(transform);
        canvas.AddChild(viewTransition);
        viewport.AddChild(canvas);
        var surface = new GodotDirectionViewSurface(viewId, viewport, canvas, transform, content, viewTransition);
        views.Add(viewId, surface);
        owner.AddChild(viewport);
        viewport.Size = ViewViewport.Size;
        SetFullRect(canvas, viewport.Size);
        SetFullRect(content, viewport.Size);
        SetFullRect(viewTransition, viewport.Size);
        viewGenerations[viewId] = 0;
        if (isInitialized) viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        return surface;
    }

    internal bool HasView(ViewId viewId) => views.ContainsKey(viewId);

    internal void ReleasePreparedView(ViewId viewId)
    {
        if (viewId == ViewId.Main || !views.Remove(viewId, out var surface)) return;
        viewGenerations.Remove(viewId);
        if (GodotObject.IsInstanceValid(surface.Viewport))
        {
            QueueFree(surface.Viewport);
        }
    }

    internal GodotDirectionViewSurface RequireView(ViewId viewId) =>
        views.TryGetValue(viewId, out var surface)
            ? surface
            : throw new DirectionReferenceException(
                $"Direction View '{viewId}' has no prepared render surface.");

    private static void QueueFree(Node node)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    private TextureRect GetOrCreateScreenSlot(LayoutSlotId slot)
    {
        if (screenSlots.TryGetValue(slot, out var existing)) return existing;
        var output = CreateTextureRect($"ScreenSlot-{slot.Value}");
        ScreenCanvas.AddChild(output);
        screenSlots.Add(slot, output);
        return output;
    }

    private static void LayoutSlot(
        TextureRect output,
        LayoutId layout,
        LayoutSlotId slot,
        Vector2I viewportSize)
    {
        var size = new Vector2(viewportSize.X, viewportSize.Y);
        output.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        output.Position = Vector2.Zero;
        output.Size = new Vector2(1, 1);
        if (layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase))
        {
            output.Position = slot.Value == "inset" ? new Vector2(size.X / 2, 0) : Vector2.Zero;
            output.Size = new Vector2(size.X / 2, size.Y);
        }
        else if (layout.Value.Equals("pip", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Value == "inset")
            {
                output.Position = new Vector2(size.X * .70f, size.Y * .06f);
                output.Size = new Vector2(size.X * .25f, size.Y * .25f);
            }
            else
            {
                output.Size = size;
            }
        }
        else output.Size = size;
    }

    private static Rect2 SlotRect(LayoutId layout, LayoutSlotId slot, Vector2I viewportSize)
    {
        var size = new Vector2(viewportSize.X, viewportSize.Y);
        if (layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase))
            return new Rect2(slot.Value == "inset" ? new Vector2(size.X / 2, 0) : Vector2.Zero,
                new Vector2(size.X / 2, size.Y));
        if (layout.Value.Equals("pip", StringComparison.OrdinalIgnoreCase) && slot.Value == "inset")
            return new Rect2(size.X * .70f, size.Y * .06f, size.X * .25f, size.Y * .25f);
        return new Rect2(Vector2.Zero, size);
    }

    private static void ResizeStage(GodotDirectionStageSurface surface, Vector2I resolution)
    {
        surface.Viewport.Size = resolution;
        SetFullRect(surface.Canvas, resolution);
        SetFullRect(surface.Background, resolution);
        if (surface.PublishedBackdrop is not null && surface.Backdrop.Texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, surface.PublishedBackdrop.Fit);
        else
            SetFullRect(surface.Backdrop, resolution);
        if (surface.TransitionBackdrop is not null && surface.BackdropTransition.Texture is not null)
            LayoutBackdrop(surface, surface.BackdropTransition, surface.TransitionBackdrop.Fit);
        else
            SetFullRect(surface.BackdropTransition, resolution);
    }

    private static void LayoutBackdrop(
        GodotDirectionStageSurface surface,
        TextureRect view,
        BackdropFit fit)
    {
        SetFullRect(view, surface.Viewport.Size);
        view.StretchMode = fit switch
        {
            BackdropFit.Cover => TextureRect.StretchModeEnum.KeepAspectCovered,
            BackdropFit.Contain => TextureRect.StretchModeEnum.KeepAspectCentered,
            BackdropFit.Stretch => TextureRect.StretchModeEnum.Scale,
            _ => throw new ArgumentOutOfRangeException(nameof(fit)),
        };
    }

    private static Color ToGodot(DirectionColor color) =>
        new(color.R, color.G, color.B, color.A);
}

internal sealed class GodotDirectionStageSurface
{
    public GodotDirectionStageSurface(
        StageId id,
        SubViewport viewport,
        Control canvas,
        ColorRect background,
        TextureRect backdrop,
        TextureRect backdropTransition,
        Node2D actors)
    {
        Id = id;
        Viewport = viewport;
        Canvas = canvas;
        Background = background;
        Backdrop = backdrop;
        BackdropTransition = backdropTransition;
        Actors = actors;
    }

    public StageId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public ColorRect Background { get; }
    public TextureRect Backdrop { get; }
    public TextureRect BackdropTransition { get; }
    public Node2D Actors { get; }
    public BackdropState? PublishedBackdrop { get; set; }
    public BackdropState? TransitionBackdrop { get; set; }
}

internal sealed class GodotDirectionViewSurface
{
    public GodotDirectionViewSurface(
        ViewId id,
        SubViewport viewport,
        Control canvas,
        Node2D transform,
        TextureRect content,
        TextureRect transitionSource)
    {
        Id = id;
        Viewport = viewport;
        Canvas = canvas;
        Transform = transform;
        Content = content;
        TransitionSource = transitionSource;
    }

    public ViewId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public Node2D Transform { get; }
    public TextureRect Content { get; }
    public TextureRect TransitionSource { get; }
    public ViewState? PublishedView { get; set; }
    public ViewPose CurrentPose { get; set; } = ViewPose.Default;
    public CameraPoseDelta ModifierDelta { get; set; } = CameraPoseDelta.Identity;
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionRenderHost : IRuntimeService, IDisposable
{
    internal readonly record struct RenderResourceMetrics(
        int StageCount,
        int ViewCount,
        int RenderTargetCount,
        long EstimatedRgba8ColorBytes);

    internal const uint BackgroundVisibilityLayer = 1u << 0;
    internal const uint SubjectVisibilityLayer = 1u << 1;
    internal const uint BackgroundTransitionVisibilityLayer = 1u << 2;
    internal const uint StageVisibilityLayers = BackgroundVisibilityLayer |
        SubjectVisibilityLayer | BackgroundTransitionVisibilityLayer;
    private readonly GodotActorModifierRegistry actorModifiers = new();
    private long targetGeneration;
    internal Func<ViewId, Task>? ReleaseViewEffectsAsync { get; set; }
    internal Func<ActorFxTarget, Task>? ReleaseActorEffectsAsync
    {
        get => actorModifiers.ReleaseActorEffectsAsync;
        set => actorModifiers.ReleaseActorEffectsAsync = value;
    }
    internal IActorModifierTarget? FindActorModifierTarget(ActorFxTarget target) => actorModifiers.Find(target);
    internal void RegisterActorModifierTarget(ActorFxTarget target, IActorModifierTarget binding) =>
        actorModifiers.Register(target, binding);
    internal Task ReleaseActorModifierTargetAsync(ActorFxTarget target, IActorModifierTarget binding) =>
        actorModifiers.ReleaseAsync(target, binding);

    private static readonly Vector2I FallbackResolution = new(1280, 720);
    private readonly Node owner;
    private readonly Vector2I configuredResolution;
    private readonly int maxStages;
    private readonly IFrameClock? frameClock;
    private readonly IRuntimeLogger logger;
    private readonly HashSet<string> coverageWarnings = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource idleCancellation = new();
    private Task? idleLoop;
    private readonly Dictionary<StageId, GodotDirectionStageSurface> stages = new();
    private readonly Dictionary<ViewId, GodotDirectionViewSurface> views = new();
    private readonly Dictionary<LayoutSlotId, TextureRect> screenSlots = new();
    private readonly Dictionary<ViewId, long> viewGenerations = new();
    private readonly Dictionary<(ViewId View, string Channel), long> environmentGenerations = new();
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
        int maxStages = 8,
        IFrameClock? frameClock = null,
        IRuntimeLogger? logger = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        configuredResolution = referenceResolution;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStages);
        this.maxStages = maxStages;
        this.frameClock = frameClock;
        this.logger = logger ?? NullRuntimeLogger.Instance;

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
        StageCanvas.VisibilityLayer = StageVisibilityLayers;
        stages[StageId.Main].BackdropFxRoot.VisibilityLayer =
            BackgroundVisibilityLayer | BackgroundTransitionVisibilityLayer;
        AssignVisibilityLayer(StageBackground, BackgroundVisibilityLayer);
        AssignVisibilityLayer(Backdrop, BackgroundVisibilityLayer);
        AssignVisibilityLayer(BackdropTransition, BackgroundTransitionVisibilityLayer);
        AssignVisibilityLayer(Actors, SubjectVisibilityLayer);

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
        ScreenTransitionSource.ZIndex = 0;
        ScreenTransitionSource.Visible = false;
        ScreenCanvas.AddChild(mainScreenSlot);
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
        OutputLayer.AddChild(ScreenTransitionSource);
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

    // Render-only cameras never participate in public View IDs or durable state.
    private readonly HashSet<GodotDirectionViewSurface> liveViewSources = new();
    private IEnumerable<GodotDirectionViewSurface> RenderViews => views.Values.Concat(liveViewSources);

    internal GodotDirectionViewSurface CreateLiveViewSource(ViewId viewId, ViewState state)
    {
        var original = RequireView(viewId);
        var id = new ViewId("live-transition-" + Guid.NewGuid().ToString("N"));
        var source = GetOrCreateView(id);
        try
        {
            PublishView(id, state with { Id = id }, GetStageTexture(state.StageId), true);
            source.ModifierDelta = original.ModifierDelta;
            ApplyViewTransform(source);
            views.Remove(id);
            viewGenerations.Remove(id);
            liveViewSources.Add(source);
            return source;
        }
        catch
        {
            ReleasePreparedView(id);
            throw;
        }
    }

    internal void ReleaseLiveViewSource(GodotDirectionViewSurface source)
    {
        if (liveViewSources.Remove(source)) QueueFree(source.Viewport);
    }

    internal RenderResourceMetrics GetRenderResourceMetrics()
    {
        long pixels = stages.Values.Sum(stage =>
            (long)stage.Viewport.Size.X * stage.Viewport.Size.Y);
        pixels += RenderViews.Sum(view =>
            (long)view.Viewport.Size.X * view.Viewport.Size.Y * 5);
        pixels += (long)ScreenViewport.Size.X * ScreenViewport.Size.Y;
        return new RenderResourceMetrics(
            stages.Count,
            views.Count + liveViewSources.Count,
            stages.Count + (views.Count + liveViewSources.Count) * 5 + 1,
            pixels * 4);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdownRequested)
            throw new DirectionException("Godot Direction render host cannot be reinitialized after shutdown.");
        if (isInitialized) return;

        owner.AddChild(StageViewport);
        owner.AddChild(ViewViewport);
        owner.AddChild(ScreenViewport);
        RenderingServer.ViewportSetParentViewport(ViewViewport.GetViewportRid(), ScreenViewport.GetViewportRid());
        BindStageRenderDependency(StageViewport);
        owner.AddChild(OutputLayer);
        Resize();
        ViewContent.Texture = StageViewport.GetTexture();
        screenSlots[LayoutSlotId.Main].Texture = ViewViewport.GetTexture();
        LiveOutput.Texture = ScreenViewport.GetTexture();
        isInitialized = true;
        PublishView(
            ViewId.Main,
            new ViewState(ViewId.Main, StageId.Main, ViewPose.Default),
            StageViewport.GetTexture());
        PublishEffects(StableEffectState.Default);
        ownerViewport = owner.GetViewport();
        ownerViewport.SizeChanged += Resize;
        viewGenerations[ViewId.Main] = ++targetGeneration;
        stageGenerations[StageId.Main] = ++targetGeneration;
        ActivateViewports();
        if (frameClock is not null) idleLoop = RunIdleLoopAsync(idleCancellation.Token);
        await WaitForRenderFenceAsync(cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken) => ResetRenderingAsync(false, cancellationToken);
    internal Task ResetSceneAsync(CancellationToken cancellationToken) => ResetRenderingAsync(true, cancellationToken);

    private Task ResetRenderingAsync(bool preserveGlobal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        InvalidateGenerations(preserveGlobal);
        coverageWarnings.Clear();
        actorModifiers.Clear();
        foreach (var surface in stages.Values.ToArray())
        {
            surface.Canvas.Modulate = Colors.White;
            surface.ModifierTarget.ApplyDelta(ActorVisualDelta.Identity);
            surface.Backdrop.Texture = null;
            surface.Backdrop.Modulate = Colors.White;
            surface.Backdrop.Visible = false;
            ClearBackdropTransition(surface.Id);
            surface.PublishedBackdrop = null;
            if (surface.Id != StageId.Main)
            {
                stages.Remove(surface.Id);
                stageGenerations.Remove(surface.Id);
                QueueFree(surface.Viewport);
            }
        }
        foreach (var view in RenderViews.ToArray())
        {
            if (view.Id == ViewId.Main) continue;
            QueueFree(view.Viewport);
            views.Remove(view.Id);
            liveViewSources.Remove(view);
            viewGenerations.Remove(view.Id);
        }
        foreach (var slot in screenSlots.Keys.Where(slot => slot != LayoutSlotId.Main).ToArray())
        {
            QueueFree(screenSlots[slot]);
            screenSlots.Remove(slot);
        }
        ViewContent.Texture = StageViewport.GetTexture();
        ViewContent.Modulate = Colors.White;
        ClearViewTransition(ViewId.Main);
        ClearViewModifierDelta(ViewId.Main);
        ClearScreenTransition();
        PublishedBackdrop = null;
        // Keep the retained surface and public state in sync. Restoring a
        // default view produces no view diff after the coordinator reset.
        PublishView(
            ViewId.Main,
            new ViewState(ViewId.Main, StageId.Main, ViewPose.Default),
            StageViewport.GetTexture());
        PublishedScreen = ScreenState.Default;
        PublishScreen(ScreenState.Default);
        if (!preserveGlobal) { PublishEffects(StableEffectState.Default); Deactivate(); }
        else Activate();
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return;
        idleCancellation.Cancel();
        if (idleLoop is not null) await idleLoop;
        retainedOutput?.Dispose();
        coverageWarnings.Clear();
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
        foreach (var view in RenderViews)
        {
            if (view.Id != ViewId.Main && GodotObject.IsInstanceValid(view.Viewport))
                QueueFree(view.Viewport);
        }
        views.Clear();
        liveViewSources.Clear();
        screenSlots.Clear();
        PublishedBackdrop = null;
        PublishedView = null;
        PublishedScreen = ScreenState.Default;
        PublishedEffects = StableEffectState.Default;
        ownerViewport = null;
        isInitialized = false;
        shutdownRequested = true;
    }

    public void PublishBackdrop(StageId stageId, BackdropState? state, Texture2D? texture)
    {
        EnsureInitialized();
        Activate();
        var surface = GetStage(stageId);
        if (state is not null)
            RegisterActorModifierTarget(new ActorFxTarget(stageId, ActorFxSlot.Backdrop), surface.ModifierTarget);

        surface.PublishedBackdrop = state;
        if (stageId == StageId.Main) PublishedBackdrop = state;
        surface.Backdrop.Texture = texture;
        surface.Backdrop.Modulate = state is null
            ? Colors.White
            : ToGodot(state.Tint);
        surface.Backdrop.Visible = state is not null && texture is not null;
        if (state is not null && texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, state);
        foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
            ApplyViewTransform(view);
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
        LayoutBackdrop(surface, surface.BackdropTransition, state);
        foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
            ApplyViewTransform(view);
    }

    public void PublishView(ViewId viewId, ViewState state, Texture2D texture, bool restoreIdlePhase = false)
    {
        EnsureInitialized();
        Activate();
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texture);
        var surface = GetOrCreateView(viewId);
        // Render the Stage world per camera so content outside the reference viewport remains visible.
        // Keep the legacy content nodes available for host inspection, but do not draw the Stage texture.
        surface.Content.Texture = null;
        surface.Canvas.Visible = false;
        surface.ContentViewport.World2D = GetStage(state.StageId).Viewport.World2D;
        surface.BackgroundViewport.World2D = GetStage(state.StageId).Viewport.World2D;
        surface.TransitionBackgroundViewport.World2D = GetStage(state.StageId).Viewport.World2D;
        surface.Content.Modulate = Colors.White;
        surface.PublishedView = state;
        ApplyViewState(viewId, state.Pose, state.Parallax, state.Idle, !restoreIdlePhase);
        if (viewId == ViewId.Main) PublishedView = state;
    }

    internal long BeginViewGeneration(ViewId viewId, string channel = "pose")
    {
        if (channel == "all")
            foreach (var key in environmentGenerations.Keys.Where(key => key.View == viewId).ToArray())
                environmentGenerations.Remove(key);
        return channel is "pose" or "all" ? viewGenerations[viewId] = ++targetGeneration
            : environmentGenerations[(viewId, channel)] = ++targetGeneration;
    }
    internal bool IsCurrentViewGeneration(ViewId viewId, long generation, string channel = "pose") =>
        (channel is "pose" or "all" ? viewGenerations.TryGetValue(viewId, out var current)
            : environmentGenerations.TryGetValue((viewId, channel), out current)) && current == generation;
    internal long BeginStageGeneration(StageId stageId)
    {
        return stageGenerations[stageId] = ++targetGeneration;
    }
    internal bool IsCurrentStageGeneration(StageId stageId, long generation) =>
        stageGenerations.TryGetValue(stageId, out var current) && current == generation;
    internal long BeginScreenGeneration() => ++screenGeneration;
    internal bool IsCurrentScreenGeneration(long generation) => screenGeneration == generation;
    internal long BeginEffectGeneration() => ++effectGeneration;
    internal bool IsCurrentEffectGeneration(long generation) => effectGeneration == generation;
    private void InvalidateGenerations(bool preserveGlobal = false)
    {
        environmentGenerations.Clear();
        screenGeneration++;
        if (!preserveGlobal) effectGeneration++;
        foreach (var id in viewGenerations.Keys.ToArray()) viewGenerations[id] = ++targetGeneration;
        foreach (var id in stageGenerations.Keys.ToArray()) stageGenerations[id] = ++targetGeneration;
    }
    internal TextureRect GetViewTransitionSource(ViewId viewId) => GetOrCreateView(viewId).TransitionSource;
    internal ViewPose GetCurrentViewPose(ViewId viewId) => GetOrCreateView(viewId).CurrentPose;
    internal ViewState GetCurrentViewState(ViewId viewId, ViewState template)
    {
        var surface = GetOrCreateView(viewId);
        return template with
        {
            Pose = surface.CurrentPose,
            Parallax = surface.CurrentParallax,
            Idle = surface.CurrentIdle,
        };
    }

    internal DirectionState SampleDynamicState(DirectionState state)
    {
        if (!isInitialized) return state;
        var sampled = state.Views.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (id, view) in sampled.ToArray())
            if (views.TryGetValue(id, out var surface))
                sampled[id] = view with { Idle = surface.CurrentIdle };
        return state with { Views = DirectionCollections.Freeze(sampled) };
    }
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
            ApplyScreenSlot(output, state, pair.Key, ScreenViewport.Size);
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
            ApplyScreenSlot(output, state, slot, ScreenViewport.Size, before, progress);
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

    internal Majo.Runtime.Operations.Godot.GodotProgressTween Animate(
        Majo.Runtime.Direction.Core.DirectionTransitionSpec transition,
        Majo.Runtime.Timing.EasingDefinition easing, Action<float> apply,
        Action complete, Action<Exception> fail) => new(owner,
            new Majo.Runtime.Timing.AnimationTiming(transition.DurationSeconds, transition.Easing),
            easing, apply, complete, fail);

    internal void ActivateOutput()
    {
        EnsureInitialized();
        Activate();
    }

    public ImageTexture CaptureView(ViewId viewId, bool canonical = false)
    {
        EnsureInitialized();
        var view = GetOrCreateView(viewId);
        Image image;
        if (!string.Equals(
            DisplayServer.GetName(),
            "headless",
            StringComparison.OrdinalIgnoreCase))
        {
            image = (canonical ? view.CompositeViewport : view.Viewport).GetTexture().GetImage();
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
        CancellationToken cancellationToken, bool canonical = false)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var view = GetOrCreateView(viewId);
        var previousMode = view.Viewport.RenderTargetUpdateMode;
        view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        try
        {
            await WaitForRenderFenceAsync(cancellationToken);
            return CaptureView(viewId, canonical);
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

    public void ApplyViewState(ViewId viewId, ViewPose pose, ViewParallaxState parallax, ViewIdleState idle,
        bool preserveIdlePhase = true)
    {
        EnsureInitialized();
        pose.Validate(); parallax.Validate(); idle.Validate();
        var surface = GetOrCreateView(viewId);
        surface.CurrentPose = pose;
        surface.CurrentParallax = parallax;
        surface.CurrentIdle = idle with
        {
            PhaseRadians = preserveIdlePhase && surface.CurrentIdle.IsEnabled
                ? surface.CurrentIdle.PhaseRadians : idle.PhaseRadians,
        };
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

    private void ApplyViewTransform(GodotDirectionViewSurface surface)
    {
        var pose = surface.CurrentPose;
        var delta = surface.ModifierDelta;
        var size = new Vector2(surface.Viewport.Size.X, surface.Viewport.Size.Y);
        var modifierBasis = new Transform2D(Mathf.DegToRad(delta.RotationDegreesOffset),
            new Vector2(delta.ZoomMultiplier, delta.ZoomMultiplier), 0, Vector2.Zero);
        var modifier = new Transform2D(modifierBasis.X, modifierBasis.Y,
            size / 2 - modifierBasis * (size / 2) + delta.CenterOffset);
        var view = surface.PublishedView;
        var distance = view is null ? 1 : stages[view.StageId].PublishedBackdrop?.Distance ?? 1;
        var projection = CameraProjectionCalculator.Calculate(
            pose,
            surface.CurrentParallax,
            surface.CurrentIdle,
            distance,
            new DirectionPoint(size.X, size.Y));
        surface.ContentViewport.CanvasTransform = modifier * ToGodot(projection.Subject, Vector2.Zero);
        surface.BackgroundViewport.CanvasTransform = modifier * ToGodot(projection.Background, Vector2.Zero);
        var transitionDistance = view is null ? distance
            : stages[view.StageId].TransitionBackdrop?.Distance ?? distance;
        var transitionProjection = CameraProjectionCalculator.Calculate(
            pose,
            surface.CurrentParallax,
            surface.CurrentIdle,
            transitionDistance,
            new DirectionPoint(size.X, size.Y));
        surface.TransitionBackgroundViewport.CanvasTransform = modifier * ToGodot(
            transitionProjection.Background, Vector2.Zero);
        surface.Transform.Transform = surface.ContentViewport.CanvasTransform;
        if (view is not null)
        {
            var stage = stages[view.StageId];
            CheckBackdropCoverage(surface, stage, stage.Backdrop, stage.PublishedBackdrop,
                FromGodot(surface.BackgroundViewport.CanvasTransform), Vector2.Zero, "target");
            CheckBackdropCoverage(surface, stage, stage.BackdropTransition, stage.TransitionBackdrop,
                FromGodot(surface.TransitionBackgroundViewport.CanvasTransform), Vector2.Zero, "source");
        }
    }

    private void CheckBackdropCoverage(
        GodotDirectionViewSurface view,
        GodotDirectionStageSurface stage,
        TextureRect visual,
        BackdropState? state,
        DirectionAffineTransform projection,
        Vector2 modifierOffset,
        string input)
    {
        if (state is null || visual.Texture is null || !visual.Visible) return;
        var bounds = BackdropVisualBounds(stage, state, visual.Texture);
        var withModifier = projection with
        {
            TX = projection.TX + modifierOffset.X,
            TY = projection.TY + modifierOffset.Y,
        };
        var result = CameraCoverageCalculator.Evaluate(
            new DirectionPoint(bounds.Position.X, bounds.Position.Y),
            new DirectionPoint(bounds.Size.X, bounds.Size.Y),
            withModifier,
            new DirectionPoint(view.Viewport.Size.X, view.Viewport.Size.Y));
        var key = $"{view.Id.Value}/{stage.Id.Value}/{input}/{state.Resource.Path}";
        if (result.IsCovered)
        {
            coverageWarnings.Remove(key);
            return;
        }
        if (!coverageWarnings.Add(key)) return;
        logger.Warning(
            "direction.camera",
            "direction.camera.backdrop_coverage",
            $"Backdrop '{state.Resource}' does not cover View '{view.Id}' after camera projection ({result.UncoveredEdges}).",
            properties: new Dictionary<string, object?>
            {
                ["view"] = view.Id.Value,
                ["stage"] = stage.Id.Value,
                ["resource"] = state.Resource.Path,
                ["transitionInput"] = input,
                ["uncoveredEdges"] = result.UncoveredEdges.ToString(),
            });
    }

    private static Rect2 BackdropVisualBounds(
        GodotDirectionStageSurface surface,
        BackdropState state,
        Texture2D texture)
    {
        if (state.Placement is not null) return BackdropBounds(surface, state, texture);
        if (state.Fit != BackdropFit.Contain) return new Rect2(Vector2.Zero, surface.Viewport.Size);
        var textureSize = texture.GetSize();
        var scale = Mathf.Min(surface.Viewport.Size.X / textureSize.X, surface.Viewport.Size.Y / textureSize.Y);
        var size = textureSize * scale;
        return new Rect2((new Vector2(surface.Viewport.Size.X, surface.Viewport.Size.Y) - size) / 2, size);
    }

    private static Transform2D ToGodot(DirectionAffineTransform value, Vector2 offset) => new(
        new Vector2(value.M11, value.M12),
        new Vector2(value.M21, value.M22),
        new Vector2(value.TX + offset.X, value.TY + offset.Y));

    private static DirectionAffineTransform FromGodot(Transform2D value) => new(
        value.X.X, value.X.Y, value.Y.X, value.Y.Y, value.Origin.X, value.Origin.Y);

    private async Task RunIdleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await frameClock!.NextFrameAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested || !GodotObject.IsInstanceValid(owner)) break;
                var delta = owner.GetTree()?.Root.GetProcessDeltaTime() ?? 0;
                foreach (var surface in RenderViews)
                {
                    if (!surface.CurrentIdle.IsEnabled) continue;
                    var phase = surface.CurrentIdle.PhaseRadians + delta * Math.Tau / surface.CurrentIdle.PeriodSeconds;
                    surface.CurrentIdle = surface.CurrentIdle with { PhaseRadians = phase % Math.Tau };
                    ApplyViewTransform(surface);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose() => idleCancellation.Dispose();

    public void ClearBackdropTransition(StageId stageId)
    {
        if (!stages.TryGetValue(stageId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.BackdropTransition)) return;
        surface.Backdrop.Visible = surface.PublishedBackdrop is not null && surface.Backdrop.Texture is not null;
        surface.BackdropTransition.Visible = false;
        surface.BackdropTransition.Texture = null;
        surface.BackdropTransition.Modulate = Colors.White;
        surface.TransitionBackdrop = null;
        if (stageId == StageId.Main) transitionBackdrop = null;
        foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
        {
            view.BackgroundOutput.Visible = true;
            view.TransitionBackgroundOutput.Visible = false;
            view.TransitionBackgroundOutput.Modulate = Colors.White;
            view.TransitionBlendOutput.Visible = false;
            view.TransitionBlendOutput.Material = null;
        }
    }

    internal IPreparedVisualTransition PrepareBackdropVisualTransition(
        StageId stageId,
        DirectionTransitionSpec transition,
        ITransitionDriver driver)
    {
        var prepared = new List<IPreparedVisualTransition>();
        try
        {
            foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
            {
                ApplyViewTransform(view);
                CanvasItem transitionSurface;
                Texture2D? sourceTexture;
                Texture2D? targetTexture;
                if (transition.Kind == DirectionTransitionId.Fade)
                {
                    view.BackgroundOutput.Visible = true;
                    view.TransitionBackgroundOutput.Visible = true;
                    view.TransitionBlendOutput.Visible = false;
                    transitionSurface = view.TransitionBackgroundOutput;
                    sourceTexture = view.TransitionBackgroundViewport.GetTexture();
                    targetTexture = view.BackgroundViewport.GetTexture();
                }
                else
                {
                    view.BackgroundOutput.Visible = false;
                    view.TransitionBackgroundOutput.Visible = false;
                    view.TransitionBlendOutput.Visible = true;
                    transitionSurface = view.TransitionBlendOutput;
                    sourceTexture = view.TransitionBackgroundViewport.GetTexture();
                    targetTexture = view.BackgroundViewport.GetTexture();
                }
                prepared.Add(driver.Prepare(new VisualTransitionContext(
                    transitionSurface,
                    transition,
                    sourceTexture,
                    targetTexture,
                    SourceIsPremultiplied: true,
                    TargetIsPremultiplied: true)));
            }
            return new PreparedVisualTransitionGroup(prepared);
        }
        catch
        {
            foreach (var item in prepared) item.Dispose();
            throw;
        }
    }

    private sealed class PreparedVisualTransitionGroup : IPreparedVisualTransition
    {
        private readonly IReadOnlyList<IPreparedVisualTransition> items;

        public PreparedVisualTransitionGroup(IReadOnlyList<IPreparedVisualTransition> items) =>
            this.items = items;

        public void ApplyProgress(float progress)
        {
            foreach (var item in items) item.ApplyProgress(progress);
        }

        public void Dispose()
        {
            foreach (var item in items) item.Dispose();
        }
    }

    public void ClearViewTransition(ViewId viewId)
    {
        if (!views.TryGetValue(viewId, out var view) ||
            !GodotObject.IsInstanceValid(view.TransitionSource)) return;
        view.CompositeOutput.Visible = true;
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
        LiveOutput.Visible = true;
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
        foreach (var view in RenderViews)
        {
            view.Viewport.Size = resolution;
            view.ContentViewport.Size = resolution;
            view.BackgroundViewport.Size = resolution;
            view.TransitionBackgroundViewport.Size = resolution;
            view.CompositeViewport.Size = resolution;
            SetFullRect(view.Output, resolution);
            SetFullRect(view.BackgroundOutput, resolution);
            SetFullRect(view.TransitionBackgroundOutput, resolution);
            SetFullRect(view.TransitionBlendOutput, resolution);
            SetFullRect(view.CompositeOutput, resolution);
            SetFullRect(view.Canvas, resolution);
            SetFullRect(view.Content, resolution);
            SetFullRect(view.TransitionSource, resolution);
            if (view.PublishedView is not null)
                ApplyViewPose(view.Id, view.CurrentPose);
        }
        var outputSize = visibleSize.X > 0 && visibleSize.Y > 0
            ? ToSize(visibleSize)
            : resolution;
        // Keep composition at the view's pixel density, rather than downsampling to
        // root canvas coordinates before the window stretches it back up. Preserve
        // the output aspect ratio for expanded windows; only the final output uses
        // logical canvas dimensions.
        var screenSize = ToSize(new Vector2(
            (float)outputSize.X / outputSize.Y * resolution.Y, resolution.Y));
        ScreenViewport.Size = screenSize;
        SetFullRect(ScreenCanvas, screenSize);
        SetFullRect(LiveOutput, outputSize);
        SetFullRect(ScreenTransitionSource, outputSize);
        SetFullRect(Curtain, outputSize);

        foreach (var pair in screenSlots)
            if (pair.Value.Visible)
                ApplyScreenSlot(pair.Value, PublishedScreen, pair.Key, screenSize);
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
        foreach (var view in RenderViews)
            if (GodotObject.IsInstanceValid(view.Viewport))
            {
                view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                view.ContentViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                view.BackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                view.TransitionBackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                view.CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            }
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
        foreach (var view in RenderViews)
            if (GodotObject.IsInstanceValid(view.Viewport))
            {
                view.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                view.ContentViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                view.BackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                view.TransitionBackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                view.CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
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
        canvas.VisibilityLayer = StageVisibilityLayers;
        surface.BackdropFxRoot.VisibilityLayer =
            BackgroundVisibilityLayer | BackgroundTransitionVisibilityLayer;
        AssignVisibilityLayer(background, BackgroundVisibilityLayer);
        AssignVisibilityLayer(backdrop, BackgroundVisibilityLayer);
        AssignVisibilityLayer(transition, BackgroundTransitionVisibilityLayer);
        AssignVisibilityLayer(actors, SubjectVisibilityLayer);
        stages.Add(stageId, surface);
        owner.AddChild(viewport);
        BindStageRenderDependency(viewport);
        ResizeStage(surface, StageViewport.Size);
        if (isInitialized)
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        return surface;
    }

    internal bool HasStage(StageId stageId) => stages.ContainsKey(stageId);

    private void BindStageRenderDependency(SubViewport viewport)
    {
        // Views draw the shared Stage World2D directly. Actor-owned render targets
        // (for example Live2D clipping masks) must finish before ANY View draws it.
        // Anchor every Stage below the permanent main View's content pass: Godot
        // sorts viewports by parent depth, placing Stages and their descendants
        // ahead of all equally deep View content/background passes, including
        // secondary Views. Parenting only to Screen or View is not deep enough.
        RenderingServer.ViewportSetParentViewport(
            viewport.GetViewportRid(),
            views[ViewId.Main].ContentViewport.GetViewportRid());
    }

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
        // Screen samples this View. Declare the dependency so a same-frame camera cut
        // renders the new pose before Screen reads its texture, regardless of creation order.
        RenderingServer.ViewportSetParentViewport(viewport.GetViewportRid(), ScreenViewport.GetViewportRid());
        viewport.Size = ViewViewport.Size;
        surface.ContentViewport.Size = viewport.Size;
        surface.BackgroundViewport.Size = viewport.Size;
        surface.TransitionBackgroundViewport.Size = viewport.Size;
        surface.CompositeViewport.Size = viewport.Size;
        SetFullRect(surface.Output, viewport.Size);
        SetFullRect(surface.BackgroundOutput, viewport.Size);
        SetFullRect(surface.TransitionBackgroundOutput, viewport.Size);
        SetFullRect(surface.TransitionBlendOutput, viewport.Size);
        SetFullRect(surface.CompositeOutput, viewport.Size);
        SetFullRect(canvas, viewport.Size);
        SetFullRect(content, viewport.Size);
        SetFullRect(viewTransition, viewport.Size);
        viewGenerations[viewId] = ++targetGeneration;
        if (isInitialized)
        {
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            surface.ContentViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            surface.BackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            surface.TransitionBackgroundViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            surface.CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
        return surface;
    }

    internal bool HasView(ViewId viewId) => views.ContainsKey(viewId);

    internal void ReleasePreparedView(ViewId viewId)
    {
        if (viewId == ViewId.Main || !views.Remove(viewId, out var surface)) return;
        viewGenerations.Remove(viewId);
        foreach (var key in environmentGenerations.Keys.Where(key => key.View == viewId).ToArray())
            environmentGenerations.Remove(key);
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

    internal static void AssignVisibilityLayer(Node root, uint layer)
    {
        if (root is CanvasItem item) item.VisibilityLayer = layer;
        foreach (var child in root.GetChildren()) AssignVisibilityLayer(child, layer);
    }

    private TextureRect GetOrCreateScreenSlot(LayoutSlotId slot)
    {
        if (screenSlots.TryGetValue(slot, out var existing)) return existing;
        var output = CreateTextureRect($"ScreenSlot-{slot.Value}");
        ScreenCanvas.AddChild(output);
        screenSlots.Add(slot, output);
        return output;
    }

    private static Rect2 SlotRect(LayoutId layout, LayoutSlotId slot, Vector2I viewportSize) =>
        DirectionLayoutCalculator.CalculateSlotRect(layout, slot, viewportSize);

    private static void ResizeStage(GodotDirectionStageSurface surface, Vector2I resolution)
    {
        surface.Viewport.Size = resolution;
        SetFullRect(surface.Canvas, resolution);
        SetFullRect(surface.Background, resolution);
        if (surface.PublishedBackdrop is not null && surface.Backdrop.Texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, surface.PublishedBackdrop);
        else
            SetFullRect(surface.Backdrop, resolution);
        if (surface.TransitionBackdrop is not null && surface.BackdropTransition.Texture is not null)
            LayoutBackdrop(surface, surface.BackdropTransition, surface.TransitionBackdrop);
        else
            SetFullRect(surface.BackdropTransition, resolution);
    }

    internal static Rect2 BackdropBounds(GodotDirectionStageSurface surface, BackdropState? state, Texture2D? texture) =>
        DirectionLayoutCalculator.CalculateBackdropBounds(
            surface.Viewport.Size,
            state?.Placement,
            texture?.GetSize() ?? Vector2.Zero);

    private static void LayoutBackdrop(
        GodotDirectionStageSurface surface,
        TextureRect view,
        BackdropState? state)
    {
        if (state?.Placement is { } placement && view.Texture is { } texture)
        {
            view.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            var rect = BackdropBounds(surface, state, texture);
            view.Position = rect.Position;
            view.Size = rect.Size;
            view.StretchMode = TextureRect.StretchModeEnum.Scale;
            return;
        }
        var fit = state?.Fit ?? BackdropFit.Stretch;
        SetFullRect(view, surface.Viewport.Size);
        view.StretchMode = DirectionLayoutCalculator.ResolveBackdropStretchMode(fit);
    }

    private static Color ToGodot(DirectionColor color) =>
        DirectionLayoutCalculator.ToGodotColor(color);
}

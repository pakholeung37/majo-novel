#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using static Majo.Runtime.Direction.Godot.GodotDirectionRenderNodes;
using Majo.Runtime.Core;
using Majo.Runtime.Actors;
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
        long EstimatedRgba8ColorBytes,
        int BackgroundContentCount = 0,
        int BackgroundSnapshotCount = 0);

    internal const uint BackgroundVisibilityLayer = GodotDirectionRenderLayers.Background;
    internal const uint SubjectVisibilityLayer = GodotDirectionRenderLayers.Subject;
    internal const uint BackgroundTransitionVisibilityLayer = GodotDirectionRenderLayers.BackgroundTransition;
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
    private readonly IFrameClock? frameClock;
    private readonly GodotDirectionViewProjection projection;
    private readonly CancellationTokenSource idleCancellation = new();
    private Task? idleLoop;
    private readonly GodotDirectionRenderNodes nodes = new();
    private readonly GodotDirectionSurfaces surfaces;
    private readonly GodotDirectionScreenComposer screen;
    private readonly GodotDirectionBackdropTransitions backdropTransitions;
    private readonly GodotDirectionRetainedOutput restoreOutput;
    private readonly Dictionary<ViewId, long> viewGenerations = new();
    private readonly Dictionary<(ViewId View, string Channel), long> environmentGenerations = new();
    private readonly Dictionary<StageId, long> stageGenerations = new();
    private long screenGeneration;
    private long effectGeneration;
    private Viewport? ownerViewport;
    private bool isInitialized;
    private bool shutdownRequested;
    private bool disposed;

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
        this.frameClock = frameClock;
        projection = new GodotDirectionViewProjection(logger ?? NullRuntimeLogger.Instance);

        surfaces = new GodotDirectionSurfaces(owner, nodes, maxStages);
        screen = new GodotDirectionScreenComposer(owner, nodes);
        backdropTransitions = new GodotDirectionBackdropTransitions(surfaces, nodes, viewDemand);
        restoreOutput = new GodotDirectionRetainedOutput(owner, screen, nodes, RenderScreenTargetAsync);
        viewDemand.Register(surfaces.MainView);
        screen.BindMainView(surfaces.MainView);
    }

    public SubViewport StageViewport => surfaces.MainStage.Viewport;
    public Control StageCanvas => surfaces.MainStage.Canvas;
    public ColorRect StageBackground => surfaces.MainStage.Background;
    public GodotDirectionBackdropVisual Backdrop => surfaces.MainStage.Backdrop;
    public GodotDirectionBackdropVisual BackdropTransition => surfaces.MainStage.BackdropTransition;
    public Node2D Actors => surfaces.MainStage.Actors;
    public SubViewport ViewViewport => surfaces.MainView.Viewport;
    public Control ViewCanvas => surfaces.MainView.Canvas;
    public Node2D ViewTransform => surfaces.MainView.Transform;
    public TextureRect ViewContent => surfaces.MainView.Content;
    public SubViewport ScreenViewport => screen.ScreenViewport;
    public Control ScreenCanvas => screen.ScreenCanvas;
    public CanvasLayer OutputLayer => screen.OutputLayer;
    public TextureRect LiveOutput => screen.LiveOutput;
    public TextureRect ScreenTransitionSource => screen.ScreenTransitionSource;
    public ColorRect Curtain => screen.Curtain;
    public BackdropState? PublishedBackdrop { get; private set; }
    public ViewState? PublishedView { get; private set; }
    public ScreenState PublishedScreen => screen.PublishedScreen;
    public StableEffectState PublishedEffects => screen.PublishedEffects;
    public IReadOnlyCollection<StageId> StageIds => surfaces.StageIds;
    public IReadOnlyCollection<ViewId> ViewIds => surfaces.ViewIds;
    internal IReadOnlyCollection<GodotDirectionViewSurface> LiveViewSources => surfaces.LiveViews;
    private IEnumerable<GodotDirectionViewSurface> RenderViews => surfaces.RenderViews;

    internal IRetainedRestoreOutput RetainRestoreOutput()
    {
        EnsureInitialized();
        var retained = restoreOutput.Retain();
        InvalidateGenerations();
        return retained;
    }
    private async Task RenderScreenTargetAsync(CancellationToken token)
    {
        using var demand = AcquireScreenDemand();
        await WaitForRenderedScreenAsync(token);
    }

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
            viewGenerations.Remove(id);
            surfaces.PromoteToLive(source);
            return source;
        }
        catch
        {
            ReleasePreparedView(id);
            throw;
        }
    }

    internal void UpdateLiveViewSource(GodotDirectionViewSurface source, ViewId original, ViewState state)
    {
        source.PublishedView = state with { Id = source.Id };
        source.CurrentPose = state.Pose;
        source.CurrentParallax = state.Parallax;
        source.CurrentIdle = state.Idle;
        source.ModifierDelta = RequireView(original).ModifierDelta;
        ApplyViewTransform(source);
    }

    internal void ReleaseLiveViewSource(GodotDirectionViewSurface source)
    {
        backdropTransitions.Remove(source);
        viewDemand.Remove(source);
        surfaces.ReleaseLive(source);
    }

    internal RenderResourceMetrics GetRenderResourceMetrics()
    {
        long pixels = surfaces.Stages.Sum(stage =>
            (long)stage.Viewport.Size.X * stage.Viewport.Size.Y);
        pixels += RenderViews.Sum(view =>
            (long)view.Viewport.Size.X * view.Viewport.Size.Y * 5);
        pixels += (long)ScreenViewport.Size.X * ScreenViewport.Size.Y;
        var rasters = GodotBackgroundRaster.Instances.Where(raster => GodotObject.IsInstanceValid(raster.Owner) &&
            surfaces.Stages.Any(stage => ReferenceEquals(stage.Viewport, raster.Owner) || stage.Viewport.IsAncestorOf(raster.Owner))).ToArray();
        var liveRasters = rasters.Count(raster => raster.HasViewport);
        return new RenderResourceMetrics(
            surfaces.StageIds.Count,
            surfaces.ViewIds.Count + surfaces.LiveViews.Count,
            surfaces.StageIds.Count + (surfaces.ViewIds.Count + surfaces.LiveViews.Count) * 5 + 1 + liveRasters,
            pixels * 4 + rasters.Sum(raster => raster.EstimatedBytes), liveRasters, rasters.Count(raster => raster.IsSnapshot));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdownRequested)
            throw new DirectionException("Godot Direction render host cannot be reinitialized after shutdown.");
        if (isInitialized) return;

        surfaces.Attach();
        screen.Attach();
        surfaces.BindRenderDependencies(ScreenViewport);
        Resize();
        ViewContent.Texture = StageViewport.GetTexture();
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
        ResetRenderDemand();
        InvalidateGenerations(preserveGlobal);
        projection.ClearDiagnostics();
        actorModifiers.Clear();
        backdropTransitions.Clear();
        foreach (var surface in surfaces.Stages.ToArray())
        {
            surface.Canvas.Modulate = Colors.White;
            surface.ModifierTarget.ApplyDelta(ActorVisualDelta.Identity);
            surface.Backdrop.Set(null, null, surface.Viewport.Size);
            surface.Backdrop.Modulate = Colors.White;
            surface.Backdrop.Visible = false;
            ClearBackdropTransition(surface.Id);
            surface.PublishedBackdrop = null;
        }
        surfaces.ResetSecondary();
        foreach (var id in stageGenerations.Keys.Where(id => id != StageId.Main).ToArray()) stageGenerations.Remove(id);
        foreach (var id in viewGenerations.Keys.Where(id => id != ViewId.Main).ToArray()) viewGenerations.Remove(id);
        screen.ResetSlots();
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
        PublishScreen(ScreenState.Default);
        if (!preserveGlobal) { PublishEffects(StableEffectState.Default); Deactivate(); }
        else Activate();
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return;
        shutdownRequested = true;
        renderCancellation.Cancel();
        viewDemand.Clear();
        idleCancellation.Cancel();
        if (idleLoop is not null) await idleLoop;
        Dispose();
    }

    public void PublishBackdrop(StageId stageId, BackdropState? state, GodotBackgroundRaster? raster)
    {
        EnsureInitialized();
        Activate();
        var surface = GetStage(stageId);
        if (state is not null)
            RegisterActorModifierTarget(new ActorFxTarget(stageId, ActorFxSlot.Backdrop), surface.ModifierTarget);

        surface.PublishedBackdrop = state;
        if (stageId == StageId.Main) PublishedBackdrop = state;
        surface.Backdrop.Set(raster, state, surface.Viewport.Size);
        foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
            ApplyViewTransform(view);
    }

    public void PublishBackdropTransition(
        StageId stageId,
        BackdropState state,
        GodotBackgroundRaster raster)
    {
        EnsureInitialized();
        var surface = GetStage(stageId);
        surface.TransitionBackdrop = state ?? throw new ArgumentNullException(nameof(state));
        surface.BackdropTransition.Set(raster, state, surface.Viewport.Size);
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
        backdropTransitions.Track(surface);
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
            if (surfaces.TryGetView(id, out var surface))
                sampled[id] = view with { Idle = surface.CurrentIdle };
        return state with { Views = new ReadOnlyDictionary<ViewId, ViewState>(sampled) };
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
        screen.PublishScreen(state, RequireView);
        RefreshScreenDemand();
    }
    internal void PublishScreenLayout(ScreenState state, ScreenState before, float progress)
    {
        EnsureInitialized();
        screen.PublishScreenLayout(state, before, progress, RequireView);
        RefreshScreenDemand();
    }
    public void PublishEffects(StableEffectState state)
    {
        EnsureInitialized();
        screen.PublishEffects(state);
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

    /// <summary>Reads the last completed frame. Use CaptureViewAsync to render current hidden content.</summary>
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
            {
                image?.Dispose();
                throw new DirectionException("Direction View capture returned an empty image.");
            }
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

        using (image) return ImageTexture.CreateFromImage(image);
    }

    internal async Task<ImageTexture> CaptureViewAsync(
        ViewId viewId,
        CancellationToken cancellationToken, bool canonical = false)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var view = GetOrCreateView(viewId);
        using var demand = viewDemand.Acquire(view, "capture");
        await WaitForRenderedViewAsync(view, demand, cancellationToken);
        return CaptureView(viewId, canonical);
    }

    internal async Task<ImageTexture> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        using var demand = AcquireScreenDemand();
        await WaitForRenderedScreenAsync(cancellationToken);
        using var captureTiming = Majo.Runtime.Diagnostics.RuntimePerformanceTrace.Source.StartActivity("direction.screen.capture.sync");
        Image image;
        if (!string.Equals(
            DisplayServer.GetName(),
            "headless",
            StringComparison.OrdinalIgnoreCase))
        {
            using (var readbackTiming = Majo.Runtime.Diagnostics.RuntimePerformanceTrace.Source.StartActivity("direction.screen.readback.sync"))
                image = ScreenViewport.GetTexture().GetImage();
            if (image is null || image.IsEmpty())
            {
                image?.Dispose();
                throw new DirectionException("Direction Screen capture returned an empty image.");
            }
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
        using (image)
        using (var uploadTiming = Majo.Runtime.Diagnostics.RuntimePerformanceTrace.Source.StartActivity("direction.screen.upload.sync"))
            return ImageTexture.CreateFromImage(image);
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
        if (!surfaces.TryGetView(viewId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.Transform)) return;
        surface.ModifierDelta = CameraPoseDelta.Identity;
        ApplyViewTransform(surface);
    }

    private void ApplyViewTransform(GodotDirectionViewSurface surface) => projection.Apply(surface,
        surface.PublishedView is { } view ? surfaces.RequireStage(view.StageId) : null);

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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdownRequested = true;
        if (ownerViewport is not null && GodotObject.IsInstanceValid(ownerViewport))
            ownerViewport.SizeChanged -= Resize;
        ownerViewport = null;
        projection.ClearDiagnostics();
        InvalidateGenerations();
        isInitialized = false;
        PublishedBackdrop = null;
        PublishedView = null;
        var failures = new List<Exception>();
        // Consumers release their textures before the surfaces and shared material.
        foreach (var release in new Action[] { renderCancellation.Cancel, idleCancellation.Cancel,
            viewDemand.Clear, restoreOutput.Dispose, backdropTransitions.Dispose, screen.Dispose,
            surfaces.Dispose, renderCancellation.Dispose, idleCancellation.Dispose, nodes.Dispose })
        {
            try { release(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) throw new AggregateException("Direction render resources cleanup failed.", failures);
    }

    public void ClearBackdropTransition(StageId stageId)
    {
        if (!surfaces.TryGetStage(stageId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.BackdropTransition)) return;
        surface.Backdrop.Visible = surface.PublishedBackdrop is not null && surface.Backdrop.Raster is not null;
        surface.BackdropTransition.Visible = false;
        surface.BackdropTransition.Set(null, null, surface.Viewport.Size);
        surface.BackdropTransition.Modulate = Colors.White;
        surface.TransitionBackdrop = null;
        foreach (var view in RenderViews.Where(view => view.PublishedView?.StageId == stageId))
        {
            view.BackgroundOutput.Visible = true;
            view.TransitionBackgroundOutput.Visible = false;
            view.TransitionBackgroundOutput.Modulate = Colors.White;
            view.TransitionBlendOutput.Visible = false;
            view.TransitionBlendOutput.Material = GodotObject.IsInstanceValid(view.RasterMaterial) ? view.RasterMaterial : null;
        }
    }

    internal GodotDirectionBackdropTransitions.Prepared PrepareBackdropVisualTransition(
        StageId stageId, DirectionTransitionSpec transition, ITransitionDriver driver, bool activate = true) =>
        backdropTransitions.Prepare(stageId, transition, driver, activate);

    public void ClearViewTransition(ViewId viewId)
    {
        if (!surfaces.TryGetView(viewId, out var view) ||
            !GodotObject.IsInstanceValid(view.TransitionSource)) return;
        view.CompositeOutput.Visible = true;
        view.TransitionSource.Visible = false;
        view.TransitionSource.Texture = null;
        view.TransitionSource.Modulate = Colors.White;
    }

    internal void PublishScreenTransition(Texture2D texture)
    {
        EnsureInitialized();
        screen.PublishScreenTransition(texture);
    }
    internal void SetScreenTransitionOpacity(float opacity) => screen.SetScreenTransitionOpacity(opacity);
    internal void ClearScreenTransition() => screen.ClearScreenTransition();

    private void Resize()
    {
        renderSizeRevision++;
        var visibleSize = owner.GetViewport().GetVisibleRect().Size;
        var resolution = configuredResolution.X > 0 && configuredResolution.Y > 0
            ? configuredResolution
            : visibleSize.X > 0 && visibleSize.Y > 0
                ? new Vector2I(
                    Math.Max(1, Mathf.RoundToInt(visibleSize.X)),
                    Math.Max(1, Mathf.RoundToInt(visibleSize.Y)))
                : FallbackResolution;
        projection.SetLogicalSize(resolution);
        var outputSize = visibleSize.X > 0 && visibleSize.Y > 0 ? ToSize(visibleSize) : resolution;
        // Layout remains in authored coordinates. Only raster targets follow the
        // window's stretch scale, avoiding a 1080p image enlarged on a 1440p display.
        // Headless rendering retains deterministic reference-sized targets.
        var stretch = owner.GetViewport().GetStretchTransform().Scale.Abs();
        var density = DisplayServer.GetName() == "headless" ? 1f :
            Mathf.Max(1f, outputSize.Y * Mathf.Max(stretch.X, stretch.Y) / resolution.Y);
        var rasterSize = ToSize((Vector2)resolution * density);
        surfaces.Resize(resolution, rasterSize);
        foreach (var view in RenderViews)
            if (view.PublishedView is not null) ApplyViewTransform(view);
        var screenSize = ToSize(new Vector2(
            (float)outputSize.X / outputSize.Y * rasterSize.Y, rasterSize.Y));
        screen.Resize(screenSize, outputSize, RequireView);
        RefreshScreenDemand();
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
        if (outputActive) return;
        outputActive = true;
        RefreshScreenDemand();
    }

    private void Deactivate()
    {
        OutputLayer.Visible = false;
        outputActive = false;
        RefreshScreenDemand();
    }

    internal GodotDirectionStageSurface GetOrCreateStage(StageId stageId)
    {
        EnsureInitialized();
        var stage = surfaces.GetOrCreateStage(stageId);
        RefreshStageRasterDemand();
        return stage;
    }
    internal bool HasStage(StageId stageId) => surfaces.HasStage(stageId);
    internal void ReleasePreparedStage(StageId stageId)
    {
        if (stageId == StageId.Main) return;
        stageGenerations.Remove(stageId);
        surfaces.ReleaseStage(stageId);
    }
    internal GodotDirectionStageSurface GetStage(StageId stageId) => surfaces.RequireStage(stageId);
    internal GodotDirectionStageSurface RequireStage(StageId stageId) => GetStage(stageId);
    internal BackdropState? GetPublishedBackdrop(StageId stageId) => GetStage(stageId).PublishedBackdrop;
    internal Texture2D GetStageTexture(StageId stageId) => GetStage(stageId).Viewport.GetTexture();

    internal GodotDirectionViewSurface GetOrCreateView(ViewId viewId)
    {
        EnsureInitialized();
        var surface = surfaces.GetOrCreateView(viewId, out var created);
        if (created)
        {
            viewGenerations[viewId] = ++targetGeneration;
            viewDemand.Register(surface);
        }
        return surface;
    }
    internal bool HasView(ViewId viewId) => surfaces.HasView(viewId);
    internal void ReleasePreparedView(ViewId viewId)
    {
        if (viewId == ViewId.Main || !surfaces.TryGetView(viewId, out var surface)) return;
        backdropTransitions.Remove(surface);
        viewDemand.Remove(surface);
        screen.RemoveView(surface);
        viewGenerations.Remove(viewId);
        foreach (var key in environmentGenerations.Keys.Where(key => key.View == viewId).ToArray())
            environmentGenerations.Remove(key);
        surfaces.ReleaseView(viewId);
    }
    internal GodotDirectionViewSurface RequireView(ViewId viewId) => surfaces.RequireView(viewId);
    internal static void AssignVisibilityLayer(Node root, uint layer) => GodotDirectionRenderNodes.AssignVisibilityLayer(root, layer);
}

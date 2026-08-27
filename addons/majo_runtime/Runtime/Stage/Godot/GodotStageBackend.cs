#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Godot;

namespace Majo.Runtime.Stage.Godot;

internal sealed class GodotStageBackend : IStageBackend
{
    private readonly Node owner;
    private readonly GodotActorHost actors;
    private readonly CanvasLayer curtainLayer;
    private readonly CanvasLayer effectLayer;
    private readonly ColorRect curtain;
    private readonly ColorRect transition;
    private readonly ColorRect overlay;
    private readonly ColorRect flash;
    private readonly ColorRect letterboxTop;
    private readonly ColorRect letterboxBottom;
    private CameraState camera = CameraState.Default;
    private StageCurtainState curtainState = StageCurtainState.Default;
    private ScreenEffectState screen = ScreenEffectState.Default;
    private Tween? cameraPositionTween;
    private Tween? cameraZoomTween;
    private Tween? cameraRotationTween;
    private Tween? shakeTween;
    private Tween? transitionTween;
    private Tween? curtainTween;
    private Tween? overlayTween;
    private Tween? letterboxTween;
    private Tween? flashTween;
    private bool isInitialized;

    public GodotStageBackend(Node owner, GodotActorHost actors)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        curtainLayer = new CanvasLayer { Name = "MajoStageCurtain", Layer = 10 };
        effectLayer = new CanvasLayer { Name = "MajoStageEffects", Layer = 90 };
        curtain = CreateFullRect("Curtain");
        transition = CreateFullRect("Transition");
        overlay = CreateFullRect("Overlay");
        flash = CreateFullRect("Flash");
        letterboxTop = CreateLetterbox("LetterboxTop", top: true);
        letterboxBottom = CreateLetterbox("LetterboxBottom", top: false);
        curtainLayer.AddChild(curtain);
        effectLayer.AddChild(transition);
        effectLayer.AddChild(overlay);
        effectLayer.AddChild(flash);
        effectLayer.AddChild(letterboxTop);
        effectLayer.AddChild(letterboxBottom);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized) return Task.CompletedTask;
        owner.AddChild(curtainLayer);
        owner.AddChild(effectLayer);
        owner.GetViewport().SizeChanged += HandleViewportSizeChanged;
        ApplyCamera(camera);
        ApplyCurtain(curtainState);
        ApplyScreenEffect(screen);
        ClearTransition();
        ClearFlash();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KillAllTweens();
        camera = CameraState.Default;
        curtainState = StageCurtainState.Default;
        screen = ScreenEffectState.Default;
        ApplyCamera(camera);
        ApplyCurtain(curtainState);
        ApplyScreenEffect(screen);
        ClearTransition();
        ClearFlash();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return Task.CompletedTask;
        isInitialized = false;
        KillAllTweens();
        try
        {
            if (GodotObject.IsInstanceValid(owner))
                owner.GetViewport().SizeChanged -= HandleViewportSizeChanged;
        }
        catch (ObjectDisposedException) { }
        try
        {
            if (GodotObject.IsInstanceValid(curtainLayer)) curtainLayer.QueueFree();
            if (GodotObject.IsInstanceValid(effectLayer)) effectLayer.QueueFree();
        }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public void ApplyCamera(CameraState state)
    {
        camera = state.Validate();
        actors.Root.Transform = CreateTransform(camera);
    }

    public Task AnimateCameraPositionAsync(
        Vector2 from, Vector2 to, double duration, CancellationToken cancellationToken) =>
        AnimateAsync(
            cameraPositionTween,
            value => cameraPositionTween = value,
            duration,
            progress => ApplyCamera(camera with { Position = from.Lerp(to, progress) }),
            cancellationToken);

    public Task AnimateCameraZoomAsync(
        float from, float to, double duration, CancellationToken cancellationToken) =>
        AnimateAsync(
            cameraZoomTween,
            value => cameraZoomTween = value,
            duration,
            progress => ApplyCamera(camera with { Zoom = Mathf.Lerp(from, to, progress) }),
            cancellationToken);

    public Task AnimateCameraRotationAsync(
        float from, float to, double duration, CancellationToken cancellationToken) =>
        AnimateAsync(
            cameraRotationTween,
            value => cameraRotationTween = value,
            duration,
            progress => ApplyCamera(camera with
            {
                RotationDegrees = Mathf.Lerp(from, to, progress),
            }),
            cancellationToken);

    public async Task ShakeAsync(CameraShakeSpec shake, CancellationToken cancellationToken)
    {
        KillTween(shakeTween);
        shakeTween = owner.CreateTween();
        var samples = Math.Max(1, (int)Math.Ceiling(
            shake.EffectiveOperation.DurationSeconds * shake.Frequency));
        var random = new Random(31);
        var sampleDuration = shake.EffectiveOperation.DurationSeconds / samples;
        for (var index = 0; index < samples; index++)
        {
            var offset = new Vector2(
                ((float)random.NextDouble() * 2 - 1) * shake.Strength.X,
                ((float)random.NextDouble() * 2 - 1) * shake.Strength.Y);
            shakeTween.TweenProperty(actors.Root, "offset", offset, sampleDuration);
        }
        shakeTween.TweenProperty(actors.Root, "offset", Vector2.Zero, sampleDuration);
        await WaitForTweenAsync(shakeTween, cancellationToken);
        shakeTween = null;
    }

    public void ClearShake()
    {
        KillTween(shakeTween);
        shakeTween = null;
        try
        {
            if (GodotObject.IsInstanceValid(actors.Root)) actors.Root.Offset = Vector2.Zero;
        }
        catch (ObjectDisposedException) { }
    }

    public async Task RunTransitionAsync(
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        KillTween(transitionTween);
        transition.Visible = true;
        transition.Color = new Color(request.Color, 0);
        transition.Scale = Vector2.One;
        transition.PivotOffset = Vector2.Zero;
        transitionTween = owner.CreateTween();
        var half = request.Operation.DurationSeconds / 2;
        if (request.Kind is StageTransitionKind.WipeLeft or StageTransitionKind.WipeRight)
        {
            transition.Color = request.Color;
            transition.Scale = new Vector2(0, 1);
            if (request.Kind == StageTransitionKind.WipeRight)
                transition.PivotOffset = new Vector2(owner.GetViewport().GetVisibleRect().Size.X, 0);
            transitionTween.TweenProperty(transition, "scale:x", 1, half);
            transitionTween.TweenProperty(transition, "modulate:a", 0, half);
        }
        else
        {
            transitionTween.TweenProperty(transition, "color:a", request.Color.A, half);
            transitionTween.TweenProperty(transition, "color:a", 0, half);
        }
        await WaitForTweenAsync(transitionTween, cancellationToken);
        transitionTween = null;
        ClearTransition();
    }

    public void ClearTransition()
    {
        KillTween(transitionTween);
        transitionTween = null;
        try
        {
            if (!GodotObject.IsInstanceValid(transition)) return;
            transition.Visible = false;
            transition.Color = new Color(Colors.Black, 0);
            transition.Modulate = Colors.White;
            transition.Scale = Vector2.One;
        }
        catch (ObjectDisposedException) { }
    }

    public void ApplyCurtain(StageCurtainState state)
    {
        curtainState = state.Validate();
        try
        {
            if (!GodotObject.IsInstanceValid(curtain)) return;
            curtain.Color = new Color(
                curtainState.Color.R,
                curtainState.Color.G,
                curtainState.Color.B,
                curtainState.Opacity);
            curtain.Visible = curtainState.Opacity > 0;
        }
        catch (ObjectDisposedException) { }
    }

    public Task AnimateCurtainAsync(
        StageCurtainState from,
        StageCurtainState to,
        double duration,
        CancellationToken cancellationToken) => AnimateAsync(
            curtainTween,
            value => curtainTween = value,
            duration,
            progress => ApplyCurtain(new StageCurtainState(
                from.Color.Lerp(to.Color, progress),
                Mathf.Lerp(from.Opacity, to.Opacity, progress))),
            cancellationToken);

    public void ApplyScreenEffect(ScreenEffectState state)
    {
        screen = state.Validate();
        overlay.Color = new Color(
            screen.OverlayColor.R,
            screen.OverlayColor.G,
            screen.OverlayColor.B,
            screen.OverlayOpacity);
        ApplyLetterbox(screen.LetterboxRatio);
    }

    public Task AnimateOverlayAsync(
        ScreenEffectState from,
        ScreenEffectState to,
        double duration,
        CancellationToken cancellationToken) => AnimateAsync(
            overlayTween,
            value => overlayTween = value,
            duration,
            progress => ApplyScreenEffect(screen with
            {
                OverlayColor = from.OverlayColor.Lerp(to.OverlayColor, progress),
                OverlayOpacity = Mathf.Lerp(from.OverlayOpacity, to.OverlayOpacity, progress),
            }),
            cancellationToken);

    public Task AnimateLetterboxAsync(
        float from,
        float to,
        double duration,
        CancellationToken cancellationToken) => AnimateAsync(
            letterboxTween,
            value => letterboxTween = value,
            duration,
            progress => ApplyScreenEffect(screen with
            {
                LetterboxRatio = Mathf.Lerp(from, to, progress),
            }),
            cancellationToken);

    public async Task FlashAsync(
        Color color,
        float intensity,
        double duration,
        CancellationToken cancellationToken)
    {
        KillTween(flashTween);
        flash.Visible = true;
        flash.Color = new Color(color.R, color.G, color.B, intensity);
        flashTween = owner.CreateTween();
        flashTween.TweenProperty(flash, "color:a", 0, duration);
        await WaitForTweenAsync(flashTween, cancellationToken);
        flashTween = null;
        ClearFlash();
    }

    public void ClearFlash()
    {
        KillTween(flashTween);
        flashTween = null;
        try
        {
            if (!GodotObject.IsInstanceValid(flash)) return;
            flash.Visible = false;
            flash.Color = new Color(Colors.White, 0);
        }
        catch (ObjectDisposedException) { }
    }

    private async Task AnimateAsync(
        Tween? existing,
        Action<Tween?> setSlot,
        double duration,
        Action<float> apply,
        CancellationToken cancellationToken)
    {
        KillTween(existing);
        if (duration <= 0)
        {
            apply(1);
            return;
        }
        var tween = owner.CreateTween();
        setSlot(tween);
        // Keep the tween values typed as floats. Integer Variants are interpolated
        // discretely by Godot, which makes fades hold at 0 and jump to 1 at the end.
        tween.TweenMethod(Callable.From<float>(apply), 0.0f, 1.0f, duration);
        await WaitForTweenAsync(tween, cancellationToken);
        setSlot(null);
    }

    private static async Task WaitForTweenAsync(Tween tween, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished() => completion.TrySetResult();
        tween.Finished += OnFinished;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try { await completion.Task; }
        finally
        {
            if (GodotObject.IsInstanceValid(tween)) tween.Finished -= OnFinished;
        }
    }

    private void ApplyLetterbox(float ratio)
    {
        var height = owner.GetViewport().GetVisibleRect().Size.Y * ratio;
        letterboxTop.OffsetBottom = height;
        letterboxBottom.OffsetTop = -height;
        letterboxTop.Visible = letterboxBottom.Visible = height > 0;
    }

    private void HandleViewportSizeChanged() => ApplyLetterbox(screen.LetterboxRatio);

    private void KillAllTweens()
    {
        KillTween(cameraPositionTween);
        KillTween(cameraZoomTween);
        KillTween(cameraRotationTween);
        KillTween(shakeTween);
        KillTween(transitionTween);
        KillTween(curtainTween);
        KillTween(overlayTween);
        KillTween(letterboxTween);
        KillTween(flashTween);
        cameraPositionTween = cameraZoomTween = cameraRotationTween = null;
        shakeTween = transitionTween = curtainTween = overlayTween = letterboxTween = flashTween = null;
    }

    private static void KillTween(Tween? tween)
    {
        if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Kill();
    }

    private static Transform2D CreateTransform(CameraState state) => new(
        Mathf.DegToRad(state.RotationDegrees),
        new Vector2(state.Zoom, state.Zoom),
        0,
        state.Position);

    private static ColorRect CreateFullRect(string name)
    {
        var result = new ColorRect
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        result.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return result;
    }

    private static ColorRect CreateLetterbox(string name, bool top)
    {
        var result = new ColorRect
        {
            Name = name,
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1,
            AnchorTop = top ? 0 : 1,
            AnchorBottom = top ? 0 : 1,
        };
        return result;
    }
}

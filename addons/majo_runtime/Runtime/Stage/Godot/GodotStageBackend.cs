#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Godot;

namespace Majo.Runtime.Stage.Godot;

internal sealed class GodotStageBackend : IStageBackend, IDisposable
{
    private readonly Node owner;
    private readonly GodotActorHost actors;
    private readonly CanvasLayer effectLayer;
    private readonly ColorRect transition;
    private CameraState camera = CameraState.Default;
    private readonly HashSet<Tween> cameraTweens = new();
    private Tween? cameraViewTransitionTween;
    private ImageTexture? cameraViewTransitionTexture;
    private Tween? shakeTween;
    private Tween? transitionTween;
    private bool isInitialized;

    public GodotStageBackend(Node owner, GodotActorHost actors)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        effectLayer = new CanvasLayer { Name = "MajoStageEffects", Layer = 90 };
        transition = CreateFullRect("Transition");
        effectLayer.AddChild(transition);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized) return Task.CompletedTask;
        owner.AddChild(effectLayer);
        owner.GetViewport().SizeChanged += HandleViewportSizeChanged;
        ApplyCamera(camera);
        ClearTransition();
        ClearCameraViewTransition();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KillAllTweens();
        camera = CameraState.Default;
        ApplyCamera(camera);
        ClearTransition();
        ClearCameraViewTransition();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return Task.CompletedTask;
        isInitialized = false;
        KillAllTweens();
        ClearCameraViewTransition();
        try
        {
            if (GodotObject.IsInstanceValid(owner))
                owner.GetViewport().SizeChanged -= HandleViewportSizeChanged;
        }
        catch (ObjectDisposedException) { }
        try
        {
            if (GodotObject.IsInstanceValid(effectLayer)) effectLayer.QueueFree();
        }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public void ApplyCamera(CameraState state)
    {
        camera = state.Validate();
        actors.Root.Transform = CreateTransform(
            camera,
            actors.StageViewport.Size);
    }

    public CameraState RenderedCamera => camera;

    public void ApplyCameraPose(CameraPoseUpdate pose) =>
        ApplyCamera(pose.Validate().Apply(camera));

    public async Task AnimateCameraAsync(
        CameraState from,
        CameraState target,
        CameraPoseUpdate properties,
        StageOperationSpec operation,
        CancellationToken cancellationToken)
    {
        void Apply(float progress) => ApplyCamera(new CameraState(
                properties.Center is not null
                    ? from.Center.Lerp(target.Center, progress)
                    : camera.Center,
                properties.Zoom is not null
                    ? Mathf.Lerp(from.Zoom, target.Zoom, progress)
                    : camera.Zoom,
                properties.RotationDegrees is not null
                    ? Mathf.LerpAngle(
                        Mathf.DegToRad(from.RotationDegrees),
                        Mathf.DegToRad(target.RotationDegrees),
                        progress) * 180 / Mathf.Pi
                    : camera.RotationDegrees));

        if (operation.DurationSeconds <= 0)
        {
            Apply(1);
            return;
        }

        var tween = owner.CreateTween();
        var (transition, ease) = ResolveCurve(operation.Curve);
        tween.SetTrans(transition);
        tween.SetEase(ease);
        cameraTweens.Add(tween);
        tween.TweenMethod(Callable.From<float>(Apply), 0.0f, 1.0f, operation.DurationSeconds);
        try
        {
            await WaitForTweenAsync(tween, cancellationToken);
        }
        finally
        {
            KillTween(tween);
            cameraTweens.Remove(tween);
        }
    }

    public async Task DissolveCameraAsync(
        CameraState from,
        CameraState target,
        StageOperationSpec operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        from.Validate();
        target.Validate();
        operation.Validate();
        ClearCameraViewTransition();
        if (operation.DurationSeconds <= 0)
        {
            ApplyCamera(target);
            return;
        }

        var image = CaptureCameraOutput();
        if (image is null || image.IsEmpty())
            throw new InvalidOperationException(
                "Camera output capture returned an empty image.");
        cameraViewTransitionTexture = ImageTexture.CreateFromImage(image);
        actors.TransitionSource.Texture = cameraViewTransitionTexture;
        actors.TransitionSource.Modulate = Colors.White;
        actors.TransitionSource.Visible = true;
        ApplyCamera(target);

        cameraViewTransitionTween = owner.CreateTween();
        var (transition, ease) = ResolveCurve(operation.Curve);
        cameraViewTransitionTween.SetTrans(transition);
        cameraViewTransitionTween.SetEase(ease);
        cameraViewTransitionTween.TweenProperty(
            actors.TransitionSource,
            "modulate:a",
            0,
            operation.DurationSeconds);
        try
        {
            await WaitForTweenAsync(cameraViewTransitionTween, cancellationToken);
        }
        finally
        {
            ClearCameraViewTransition();
        }
    }

    private Image CaptureCameraOutput()
    {
        if (!string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            return actors.StageViewport.GetTexture().GetImage();

        // Godot's dummy headless renderer exposes a ViewportTexture but has no
        // native texture storage to read back. Keep the transition lifecycle
        // testable without pretending that pixel output exists in this mode.
        var size = actors.StageViewport.Size;
        var image = Image.CreateEmpty(
            Math.Max(size.X, 1),
            Math.Max(size.Y, 1),
            false,
            Image.Format.Rgba8);
        image.Fill(Colors.Black);
        return image;
    }

    public void ClearCameraViewTransition()
    {
        KillTween(cameraViewTransitionTween);
        cameraViewTransitionTween = null;
        try
        {
            if (GodotObject.IsInstanceValid(actors.TransitionSource))
            {
                actors.TransitionSource.Visible = false;
                actors.TransitionSource.Texture = null;
                actors.TransitionSource.Modulate = Colors.White;
            }
        }
        catch (ObjectDisposedException) { }
        cameraViewTransitionTexture?.Dispose();
        cameraViewTransitionTexture = null;
    }

    public async Task ShakeAsync(CameraShakeSpec shake, CancellationToken cancellationToken)
    {
        KillTween(shakeTween);
        shakeTween = owner.CreateTween();
        var samples = Math.Max(1, (int)Math.Ceiling(
            shake.EffectiveOperation.DurationSeconds * shake.Frequency));
        var random = Random.Shared;
        var sampleDuration = shake.EffectiveOperation.DurationSeconds / samples;
        var initialSign = random.Next(2) == 0 ? 1f : -1f;
        var initialAngle = (float)(random.NextDouble() * Math.PI * 2);

        for (var index = 0; index < samples; index++)
        {
            var progress = (float)index / samples;
            var decay = (1f - progress) * (1f - progress);
            var sign = (index % 2 == 0 ? 1f : -1f) * initialSign;
            var noise = 0.7f + 0.3f * (float)random.NextDouble();
            var t = sign * noise * shake.Strength * decay;

            Vector2 offset;
            switch (shake.Axis)
            {
                case CameraShakeAxis.X:
                    offset = new Vector2(t, 0);
                    break;
                case CameraShakeAxis.Y:
                    offset = new Vector2(0, t);
                    break;
                case CameraShakeAxis.Diagonal:
                    offset = new Vector2(t, t);
                    break;
                default:
                    {
                        var angle = initialAngle + index * MathF.PI + (float)((random.NextDouble() * 0.6) - 0.3);
                        var radius = noise * shake.Strength * decay;
                        offset = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                        break;
                    }
            }

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

    private async Task AnimateAsync(
        Tween? existing,
        Action<Tween?> setSlot,
        double duration,
        Action<float> apply,
        CancellationToken cancellationToken,
        MotionCurve curve = MotionCurve.Linear)
    {
        KillTween(existing);
        if (duration <= 0)
        {
            apply(1);
            return;
        }
        var tween = owner.CreateTween();
        var (transition, ease) = ResolveCurve(curve);
        tween.SetTrans(transition);
        tween.SetEase(ease);
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

    private void KillAllTweens()
    {
        foreach (var tween in cameraTweens.ToArray()) KillTween(tween);
        cameraTweens.Clear();
        KillTween(cameraViewTransitionTween);
        KillTween(shakeTween);
        KillTween(transitionTween);
        cameraViewTransitionTween = shakeTween = transitionTween = null;
    }

    private static void KillTween(Tween? tween)
    {
        if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Kill();
    }

    private static Transform2D CreateTransform(CameraState state, Vector2 viewportSize)
    {
        var basis = new Transform2D(
            Mathf.DegToRad(state.RotationDegrees),
            new Vector2(state.Zoom, state.Zoom),
            0,
            Vector2.Zero);
        var sceneCenter = new Vector2(
            viewportSize.X * state.Center.X / 100,
            viewportSize.Y * (1 - state.Center.Y / 100));
        var viewportCenter = viewportSize / 2;
        return new Transform2D(
            basis.X,
            basis.Y,
            viewportCenter - basis * sceneCenter);
    }

    private static (Tween.TransitionType Transition, Tween.EaseType Ease) ResolveCurve(
        MotionCurve curve) => curve switch
        {
            MotionCurve.Linear => (Tween.TransitionType.Linear, Tween.EaseType.InOut),
            MotionCurve.SineIn => (Tween.TransitionType.Sine, Tween.EaseType.In),
            MotionCurve.SineOut => (Tween.TransitionType.Sine, Tween.EaseType.Out),
            MotionCurve.SineInOut => (Tween.TransitionType.Sine, Tween.EaseType.InOut),
            MotionCurve.CubicIn => (Tween.TransitionType.Cubic, Tween.EaseType.In),
            MotionCurve.CubicOut => (Tween.TransitionType.Cubic, Tween.EaseType.Out),
            MotionCurve.CubicInOut => (Tween.TransitionType.Cubic, Tween.EaseType.InOut),
            MotionCurve.QuintIn => (Tween.TransitionType.Quint, Tween.EaseType.In),
            MotionCurve.QuintOut => (Tween.TransitionType.Quint, Tween.EaseType.Out),
            MotionCurve.QuintInOut => (Tween.TransitionType.Quint, Tween.EaseType.InOut),
            MotionCurve.BackIn => (Tween.TransitionType.Back, Tween.EaseType.In),
            MotionCurve.BackOut => (Tween.TransitionType.Back, Tween.EaseType.Out),
            MotionCurve.BackInOut => (Tween.TransitionType.Back, Tween.EaseType.InOut),
            _ => throw new ArgumentOutOfRangeException(nameof(curve)),
        };

    private void HandleViewportSizeChanged() => ApplyCamera(camera);

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

    public void Dispose()
    {
        ClearCameraViewTransition();
        if (GodotObject.IsInstanceValid(effectLayer))
        {
            effectLayer.QueueFree();
            effectLayer.Dispose();
        }
    }
}

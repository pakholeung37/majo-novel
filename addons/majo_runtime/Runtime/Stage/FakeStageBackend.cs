#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Stage;

public sealed class FakeStageBackend : IStageBackend
{
    public CameraState Camera { get; private set; } = CameraState.Default;
    public CameraState RenderedCamera => Camera;
    public StageCurtainState Curtain { get; private set; } = StageCurtainState.Default;
    public ScreenEffectState ScreenEffect { get; private set; } = ScreenEffectState.Default;
    public bool IsShaking { get; private set; }
    public bool IsTransitioning { get; private set; }
    public bool IsFlashing { get; private set; }
    public StageOperationSpec? LastCameraOperation { get; private set; }
    public CameraViewTransitionKind? LastCameraTransition { get; private set; }
    public CameraState? LastDissolveSource { get; private set; }
    public CameraState? LastDissolveTarget { get; private set; }
    public int ActiveCameraCaptureCount { get; private set; }
    public List<string> CameraTransitionCalls { get; } = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken)
    {
        Camera = CameraState.Default;
        Curtain = StageCurtainState.Default;
        ScreenEffect = ScreenEffectState.Default;
        IsShaking = IsTransitioning = IsFlashing = false;
        LastCameraOperation = null;
        LastCameraTransition = null;
        LastDissolveSource = LastDissolveTarget = null;
        ActiveCameraCaptureCount = 0;
        CameraTransitionCalls.Clear();
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken cancellationToken) => ResetAsync(cancellationToken);
    public void ApplyCamera(CameraState state) => Camera = state.Validate();
    public void ApplyCameraPose(CameraPoseUpdate pose) =>
        Camera = pose.Validate().Apply(Camera).Validate();
    public Task AnimateCameraAsync(
        CameraState from,
        CameraState to,
        CameraPoseUpdate properties,
        StageOperationSpec operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCameraOperation = operation;
        LastCameraTransition = CameraViewTransitionKind.Move;
        ApplyCameraPose(properties);
        return Task.CompletedTask;
    }
    public Task DissolveCameraAsync(
        CameraState from,
        CameraState to,
        StageOperationSpec operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCameraOperation = operation;
        LastCameraTransition = CameraViewTransitionKind.Dissolve;
        LastDissolveSource = from.Validate();
        LastDissolveTarget = to.Validate();
        CameraTransitionCalls.Add("CaptureSource");
        ActiveCameraCaptureCount++;
        CameraTransitionCalls.Add("ApplyTargetPose");
        ApplyCamera(to);
        CameraTransitionCalls.Add("ShowSourceOverlay");
        CameraTransitionCalls.Add("AnimateSourceAlpha");
        ClearCameraViewTransition();
        return Task.CompletedTask;
    }
    public void ClearCameraViewTransition()
    {
        if (ActiveCameraCaptureCount > 0)
            CameraTransitionCalls.Add("ReleaseSource");
        ActiveCameraCaptureCount = 0;
    }
    public Task ShakeAsync(CameraShakeSpec shake, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsShaking = true;
        IsShaking = false;
        return Task.CompletedTask;
    }
    public void ClearShake() => IsShaking = false;
    public Task RunTransitionAsync(StageTransitionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsTransitioning = true;
        IsTransitioning = false;
        return Task.CompletedTask;
    }
    public void ClearTransition() => IsTransitioning = false;
    public void ApplyCurtain(StageCurtainState state) => Curtain = state.Validate();
    public Task AnimateCurtainAsync(
        StageCurtainState from,
        StageCurtainState to,
        double duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Curtain = to.Validate();
        return Task.CompletedTask;
    }
    public void ApplyScreenEffect(ScreenEffectState state) => ScreenEffect = state.Validate();
    public Task AnimateOverlayAsync(ScreenEffectState from, ScreenEffectState to, double duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScreenEffect = ScreenEffect with { OverlayColor = to.OverlayColor, OverlayOpacity = to.OverlayOpacity };
        return Task.CompletedTask;
    }
    public Task AnimateLetterboxAsync(float from, float to, double duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScreenEffect = ScreenEffect with { LetterboxRatio = to };
        return Task.CompletedTask;
    }
    public Task FlashAsync(Color color, float intensity, double duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsFlashing = true;
        IsFlashing = false;
        return Task.CompletedTask;
    }
    public void ClearFlash() => IsFlashing = false;
}

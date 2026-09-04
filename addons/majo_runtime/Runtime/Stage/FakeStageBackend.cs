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
    public bool IsShaking { get; private set; }
    public CameraShakeSpec? LastShake { get; private set; }
    public bool IsTransitioning { get; private set; }
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
        IsShaking = IsTransitioning = false;
        LastShake = null;
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
        CameraState target,
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
        CameraState target,
        StageOperationSpec operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCameraOperation = operation;
        LastCameraTransition = CameraViewTransitionKind.Dissolve;
        LastDissolveSource = from.Validate();
        LastDissolveTarget = target.Validate();
        CameraTransitionCalls.Add("CaptureSource");
        ActiveCameraCaptureCount++;
        CameraTransitionCalls.Add("ApplyTargetPose");
        ApplyCamera(target);
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
        LastShake = shake;
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
}

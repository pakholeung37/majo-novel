#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Stage;

public interface IStageAccessibility : IRuntimeService
{
    bool ReduceMotion { get; set; }
}

public interface ICameraManager : IRuntimeService
{
    CameraState State { get; }
    CameraState TargetState { get; }
    IOperationHandle TransitionTo(CameraPoseTransitionRequest request);
    IOperationHandle PoseTo(CameraPoseUpdate pose, StageOperationSpec? operation = null);
    IOperationHandle MoveTo(Vector2 position, StageOperationSpec? operation = null);
    IOperationHandle ZoomTo(float zoom, StageOperationSpec? operation = null);
    IOperationHandle RotateTo(float degrees, StageOperationSpec? operation = null);
    IOperationHandle ResetCamera(CameraViewTransitionSpec transition);
    IOperationHandle Shake(CameraShakeSpec shake);
    void ApplyStableState(CameraState state);
}

public interface IStageTransitionManager : IRuntimeService
{
    string? Appearance { get; }
    IReadOnlyCollection<string> ProviderIds { get; }
    IOperationHandle Run(StageTransitionRequest request);
    void ApplyStableAppearance(string? appearance);
}

public interface IStageTransitionProvider
{
    string Id { get; }
    Task RunAsync(
        IStageBackend backend,
        StageTransitionRequest request,
        CancellationToken cancellationToken);
    void Complete(IStageBackend backend, StageTransitionRequest request);
    void Cancel(IStageBackend backend, StageTransitionRequest request);
}

public interface IStageBackend : IRuntimeService
{
    CameraState RenderedCamera { get; }
    void ApplyCamera(CameraState state);
    void ApplyCameraPose(CameraPoseUpdate pose);
    Task AnimateCameraAsync(
        CameraState from,
        CameraState target,
        CameraPoseUpdate properties,
        StageOperationSpec operation,
        CancellationToken cancellationToken);
    Task DissolveCameraAsync(
        CameraState from,
        CameraState target,
        StageOperationSpec operation,
        CancellationToken cancellationToken);
    void ClearCameraViewTransition();
    Task ShakeAsync(CameraShakeSpec shake, CancellationToken cancellationToken);
    void ClearShake();
    Task RunTransitionAsync(StageTransitionRequest request, CancellationToken cancellationToken);
    void ClearTransition();
}

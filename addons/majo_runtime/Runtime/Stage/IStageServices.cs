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
    IOperationHandle MoveTo(Vector2 position, StageOperationSpec? operation = null);
    IOperationHandle ZoomTo(float zoom, StageOperationSpec? operation = null);
    IOperationHandle RotateTo(float degrees, StageOperationSpec? operation = null);
    IOperationHandle Focus(Vector2 position, float zoom, StageOperationSpec? operation = null);
    IOperationHandle ResetCamera(StageOperationSpec? operation = null);
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

public interface IStageCurtainManager : IRuntimeService
{
    StageCurtainState State { get; }
    StageCurtainState TargetState { get; }
    IOperationHandle SetCurtain(
        Color color,
        float opacity,
        StageOperationSpec? operation = null);
    void ApplyStableState(StageCurtainState state);
}

public interface IScreenEffectManager : IRuntimeService
{
    ScreenEffectState State { get; }
    ScreenEffectState TargetState { get; }
    IOperationHandle SetOverlay(
        Color color,
        float opacity,
        StageOperationSpec? operation = null);
    IOperationHandle SetLetterbox(float ratio, StageOperationSpec? operation = null);
    IOperationHandle Flash(
        Color color,
        float intensity = 1,
        StageOperationSpec? operation = null);
    void ApplyStableState(ScreenEffectState state);
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
    void ApplyCamera(CameraState state);
    Task AnimateCameraPositionAsync(Vector2 from, Vector2 to, double duration, CancellationToken cancellationToken);
    Task AnimateCameraZoomAsync(float from, float to, double duration, CancellationToken cancellationToken);
    Task AnimateCameraRotationAsync(float from, float to, double duration, CancellationToken cancellationToken);
    Task ShakeAsync(CameraShakeSpec shake, CancellationToken cancellationToken);
    void ClearShake();
    Task RunTransitionAsync(StageTransitionRequest request, CancellationToken cancellationToken);
    void ClearTransition();
    void ApplyCurtain(StageCurtainState state);
    Task AnimateCurtainAsync(
        StageCurtainState from,
        StageCurtainState to,
        double duration,
        CancellationToken cancellationToken);
    void ApplyScreenEffect(ScreenEffectState state);
    Task AnimateOverlayAsync(
        ScreenEffectState from,
        ScreenEffectState to,
        double duration,
        CancellationToken cancellationToken);
    Task AnimateLetterboxAsync(float from, float to, double duration, CancellationToken cancellationToken);
    Task FlashAsync(Color color, float intensity, double duration, CancellationToken cancellationToken);
    void ClearFlash();
}

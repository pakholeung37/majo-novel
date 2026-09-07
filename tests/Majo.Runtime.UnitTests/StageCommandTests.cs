using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StageCommandTests
{
    [Fact]
    public void DirectionCameraCommandsBuildAValidatedCatalog()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var services = new ServiceResolverBuilder()
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDirectionScopeResolver>(scopes)
            .Add<IDialogueNodeAccessor>(nodeAccessor)
            .Add<IDirectionCommandPolicy>(policy)
            .Add<IDirectionResourceKeyResolver>(new CatalogResourceResolver())
            .Add<IDirectionPositionResolver>(new CatalogPositionResolver())
            .Build();

        var registry = new RuntimeCommandRegistry();
        new DirectionCommandModule().Register(registry);
        registry.SealRegistrations();
        var catalog = registry.BuildCatalog(services);

        Assert.Contains(catalog.Commands, command => command.Name == "camera move");
        Assert.Contains(catalog.Commands, command => command.Name == "camera pose");
        Assert.Contains(catalog.Commands, command => command.Name == "camera reset");
        Assert.Contains(catalog.Commands, command => command.Name == "camera fx");

        var move = (CameraMoveCommand)catalog.Get("camera move");
        var moveArgs = move.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["center"] = "12,-4",
            ["ease"] = "back-out",
            ["wait"] = "true"
        }));
        Assert.Equal(12, moveArgs.Center.Value.X);
        Assert.Equal(-4, moveArgs.Center.Value.Y);
        Assert.Equal(EasingId.BackOut, moveArgs.Timing.Ease.Value);
        Assert.True(moveArgs.Timing.Wait.Value);

        var pose = (CameraPoseCommand)catalog.Get("camera pose");
        var poseArguments = pose.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["center"] = "80,-35",
            ["zoom"] = "3",
            ["rotation"] = "-10"
        }));
        Assert.Equal(80, poseArguments.Center.Value.X);
        Assert.Equal(-35, poseArguments.Center.Value.Y);
        Assert.Equal(3, poseArguments.Zoom.Value);
        Assert.Equal(-10, poseArguments.Rotation.Value);
        Assert.False(poseArguments.Via.WasProvided);
        Assert.False(poseArguments.Timing.Duration.WasProvided);

        var dissolveArguments = pose.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["zoom"] = "1",
            ["via"] = "dissolve",
            ["duration"] = "0.6",
            ["ease"] = "sine-in-out"
        }));
        Assert.Equal(DirectionTransitionId.Dissolve, dissolveArguments.Via.Value);
        Assert.Equal(0.6, dissolveArguments.Timing.Duration.Value);
        Assert.Equal(EasingId.SineInOut, dissolveArguments.Timing.Ease.Value);

        var reset = (CameraResetCommand)catalog.Get("camera reset");
        var resetArguments = reset.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["via"] = "dissolve",
            ["duration"] = "0.6",
            ["wait"] = "true"
        }));
        Assert.False(resetArguments.Stage.WasProvided);
        Assert.False(resetArguments.View.WasProvided);
        Assert.Equal(DirectionTransitionId.Dissolve, resetArguments.Transition.Via.Value);
        Assert.Equal(0.6, resetArguments.Transition.Timing.Duration.Value);
        Assert.True(resetArguments.Transition.Wait.Value);

        var resetStage = reset.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["stage"] = "main"
        }));
        Assert.Equal("main", resetStage.Stage.Value);
        Assert.False(resetStage.View.WasProvided);

        var resetAmbiguous = Assert.Throws<RuntimeCommandException>(() =>
            reset.Schema.Bind(new RawCommandArguments(null, new Dictionary<string, string>
            {
                ["stage"] = "main",
                ["view"] = "main"
            })));
        Assert.Contains("stage", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("view", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);

        var fx = (CameraFxCommand)catalog.Get("camera fx");
        var fxArguments = fx.Schema.Bind(new RawCommandArguments("shake", new Dictionary<string, string>
        {
            ["stage"] = "illustration",
            ["strength"] = "15",
            ["axis"] = "diagonal",
            ["frequency"] = "30",
            ["duration"] = "0.8",
            ["wait"] = "true"
        }));
        Assert.Equal("shake", fxArguments.Effect);
        Assert.Equal("illustration", fxArguments.Stage.Value);
        Assert.Equal(15, fxArguments.Strength.Value);
        Assert.Equal(CameraShakeAxis.Diagonal, fxArguments.Axis.Value);
        Assert.Equal(30, fxArguments.Frequency.Value);
        Assert.Equal(0.8, fxArguments.Duration.Value);
        Assert.True(fxArguments.Wait.Wait.Value);
    }

    [Fact]
    public void CameraCutRejectsANonZeroDuration()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new CameraViewTransitionSpec(
                CameraViewTransitionKind.Cut,
                new StageOperationSpec(0.1)).Validate());

        Assert.Contains("duration must be zero", exception.Message);
    }

    [Fact(Timeout = 5_000)]
    public async Task CameraDissolveCapturesAppliesAndReleasesTheTargetView()
    {
        var backend = new FakeStageBackend();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IFrameClock>(_ => new ImmediateFrameClock())
            .AddSingleton<IOperationRunner>(
                resolver => new OperationRunner(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IStageBackend>(_ => backend)
            .AddSingleton<IStageAccessibility>(_ => new StageAccessibility())
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationRunner>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationRunner),
                typeof(IStageAccessibility));
        var host = registry.Build();
        await host.InitializeAsync();
        var camera = host.Services.Get<ICameraManager>();
        var focus = new CameraState(new Godot.Vector2(37.384f, 74.816f), 3, 10);
        camera.ApplyStableState(focus);

        var operation = camera.TransitionTo(new CameraPoseTransitionRequest(
            new CameraPoseUpdate(
                CameraState.Default.Center,
                CameraState.Default.Zoom,
                CameraState.Default.RotationDegrees),
            new CameraViewTransitionSpec(
                CameraViewTransitionKind.Dissolve,
                new StageOperationSpec(0.6, Curve: MotionCurve.SineInOut))));
        await operation.Completion;

        Assert.Equal(CameraState.Default, camera.State);
        Assert.Equal(CameraState.Default, camera.TargetState);
        Assert.Equal(focus, backend.LastDissolveSource);
        Assert.Equal(CameraState.Default, backend.LastDissolveTarget);
        Assert.Equal(
            new[]
            {
                "CaptureSource",
                "ApplyTargetPose",
                "ShowSourceOverlay",
                "AnimateSourceAlpha",
                "ReleaseSource",
            },
            backend.CameraTransitionCalls);
        Assert.Equal(0, backend.ActiveCameraCaptureCount);
        await host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CameraPoseCommitsAllPropertiesAsOneOperation()
    {
        var backend = new FakeStageBackend();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IFrameClock>(_ => new ImmediateFrameClock())
            .AddSingleton<IOperationRunner>(
                resolver => new OperationRunner(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IStageBackend>(_ => backend)
            .AddSingleton<IStageAccessibility>(_ => new StageAccessibility())
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationRunner>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationRunner),
                typeof(IStageAccessibility));
        var host = registry.Build();
        await host.InitializeAsync();
        var camera = host.Services.Get<ICameraManager>();

        var operation = camera.PoseTo(
            new CameraPoseUpdate(new Godot.Vector2(80, -35), 3, -10),
            new StageOperationSpec(0.4, Curve: MotionCurve.BackOut));
        await operation.Completion;

        Assert.Equal(new Godot.Vector2(80, -35), camera.State.Center);
        Assert.Equal(3, camera.State.Zoom);
        Assert.Equal(-10, camera.State.RotationDegrees);
        Assert.Equal(camera.State, backend.Camera);
        Assert.Equal(MotionCurve.BackOut, backend.LastCameraOperation?.Curve);
        await host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CameraShakeDispatchesStrengthBasedOnAxis()
    {
        var backend = new FakeStageBackend();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IFrameClock>(_ => new ImmediateFrameClock())
            .AddSingleton<IOperationRunner>(
                resolver => new OperationRunner(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IStageBackend>(_ => backend)
            .AddSingleton<IStageAccessibility>(_ => new StageAccessibility())
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationRunner>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationRunner),
                typeof(IStageAccessibility));
        var host = registry.Build();
        await host.InitializeAsync();
        var camera = host.Services.Get<ICameraManager>();

        var operation = camera.Shake(new CameraShakeSpec(16f, CameraShakeAxis.X, 25, new StageOperationSpec(0.5)));
        await operation.Completion;

        Assert.NotNull(backend.LastShake);
        Assert.Equal(16f, backend.LastShake!.Strength);
        Assert.Equal(CameraShakeAxis.X, backend.LastShake.Axis);
        Assert.Equal(25, backend.LastShake.Frequency);
        Assert.Equal(0.5, backend.LastShake.EffectiveOperation.DurationSeconds);
        await host.ShutdownAsync();
    }

    private sealed class ImmediateFrameClock : IFrameClock
    {
        public Task NextFrameAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

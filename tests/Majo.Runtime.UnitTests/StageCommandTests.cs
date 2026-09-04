using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StageCommandTests
{
    [Fact]
    public void DirectionCameraCommandsBuildAValidatedCatalog()
    {
        var registry = new RuntimeCommandRegistry();
        registry
            .Register<CameraMoveDirectionCommandArguments>((_, _) => Task.CompletedTask)
            .Register<CameraPoseDirectionCommandArguments>((_, _) => Task.CompletedTask)
            .Register<CameraResetDirectionCommandArguments>((_, _) => Task.CompletedTask)
            .Register<CameraFxDirectionCommandArguments>((_, _) => Task.CompletedTask);
        var catalog = registry.BuildCatalog();

        Assert.Contains(catalog.Commands, command => command.Name == "camera move");
        Assert.Contains(catalog.Commands, command => command.Name == "camera pose");
        Assert.Contains(catalog.Commands, command => command.Name == "camera reset");
        Assert.Contains(catalog.Commands, command => command.Name == "camera fx");

        var binder = new CommandBinder(new CommandBindingContext(0.35));
        foreach (var command in catalog.Commands) binder.Validate(command.Descriptor);

        var move = catalog.Get("camera move");
        var arguments = binder.Bind<CameraMoveDirectionCommandArguments>(
            move.Descriptor, new[] { "center:12,-4", "ease:back-out", "wait:true" });
        Assert.Equal(12, arguments.Center?.X);
        Assert.Equal(-4, arguments.Center?.Y);
        Assert.Equal("back-out", arguments.Ease);
        Assert.True(arguments.Wait);

        var pose = catalog.Get("camera pose");
        var poseArguments = binder.Bind<CameraPoseDirectionCommandArguments>(
            pose.Descriptor, new[] { "center:80,-35", "zoom:3", "rotation:-10" });
        Assert.Equal(80, poseArguments.Center?.X);
        Assert.Equal(-35, poseArguments.Center?.Y);
        Assert.Equal(3, poseArguments.Zoom);
        Assert.Equal(-10, poseArguments.Rotation);
        Assert.Null(poseArguments.Via);
        Assert.Null(poseArguments.Duration);

        var dissolveArguments = binder.Bind<CameraPoseDirectionCommandArguments>(
            pose.Descriptor,
            new[] { "zoom:1", "via:dissolve", "duration:0.6", "ease:sine-in-out" });
        Assert.Equal(DirectionTransitionKind.Dissolve, dissolveArguments.Via);
        Assert.Equal(0.6, dissolveArguments.Duration);
        Assert.Equal("sine-in-out", dissolveArguments.Ease);

        var reset = catalog.Get("camera reset");
        var resetArguments = binder.Bind<CameraResetDirectionCommandArguments>(
            reset.Descriptor,
            new[] { "via:dissolve", "duration:0.6", "wait:true" });
        Assert.Null(resetArguments.Stage);
        Assert.Null(resetArguments.View);
        Assert.Equal(DirectionTransitionKind.Dissolve, resetArguments.Via);
        Assert.Equal(0.6, resetArguments.Duration);
        Assert.True(resetArguments.Wait);
        var resetStage = binder.Bind<CameraResetDirectionCommandArguments>(
            reset.Descriptor,
            new[] { "stage:main" });
        Assert.Equal("main", resetStage.Stage);
        Assert.Null(resetStage.View);
        var resetAmbiguous = Assert.Throws<RuntimeCommandException>(() =>
            binder.Bind<CameraResetDirectionCommandArguments>(
                reset.Descriptor,
                new[] { "stage:main", "view:main" }));
        Assert.Contains("stage", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("view", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);

        var fx = catalog.Get("camera fx");
        var fxArguments = binder.Bind<CameraFxDirectionCommandArguments>(
            fx.Descriptor,
            new[] { "shake", "stage:illustration", "strength:15", "axis:diagonal", "frequency:30", "duration:0.8", "wait:true" });
        Assert.Equal("shake", fxArguments.Effect);
        Assert.Equal("illustration", fxArguments.Stage);
        Assert.Equal(15, fxArguments.Strength);
        Assert.Equal(CameraShakeAxis.Diagonal, fxArguments.Axis);
        Assert.Equal(30, fxArguments.Frequency);
        Assert.Equal(0.8, fxArguments.Duration);
        Assert.True(fxArguments.Wait);
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

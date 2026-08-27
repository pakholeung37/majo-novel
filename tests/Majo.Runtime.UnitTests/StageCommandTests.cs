using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StageCommandTests
{
    [Fact]
    public void StageCommandModuleBuildsAValidatedCatalog()
    {
        var registry = new RuntimeCommandRegistry();
        registry.RegisterModule(new StageCommandModule());
        var catalog = registry.BuildCatalog();

        Assert.Contains(catalog.Commands, command => command.Name == "camera move");
        Assert.Contains(catalog.Commands, command => command.Name == "camera pose");
        Assert.Contains(catalog.Commands, command => command.Name == "transition");
        Assert.Contains(catalog.Commands, command => command.Name == "curtain");
        Assert.Contains(catalog.Commands, command => command.Name == "flash");

        var binder = new CommandBinder(new CommandBindingContext(0.35));
        foreach (var command in catalog.Commands) binder.Validate(command.Descriptor);

        var move = catalog.Get("camera move");
        var arguments = binder.Bind<CameraMoveCommandArguments>(
            move.Descriptor, new[] { "to:12,-4", "curve:back-out", "wait:true" });
        Assert.Equal(12, arguments.To.X);
        Assert.Equal(-4, arguments.To.Y);
        Assert.Equal(MotionCurve.BackOut, arguments.Curve);
        Assert.True(arguments.Wait);

        var pose = catalog.Get("camera pose");
        var poseArguments = binder.Bind<CameraPoseCommandArguments>(
            pose.Descriptor, new[] { "center:80,-35", "zoom:3", "rotation:-10" });
        Assert.Equal(80, poseArguments.Center?.X);
        Assert.Equal(-35, poseArguments.Center?.Y);
        Assert.Equal(3, poseArguments.Zoom);
        Assert.Equal(-10, poseArguments.Rotation);

        var curtain = catalog.Get("curtain");
        var curtainArguments = binder.Bind<StageCurtainCommandArguments>(
            curtain.Descriptor, new[] { "closed", "duration:1.2", "wait:true" });
        Assert.Equal(StageCurtainPosition.Closed, curtainArguments.Position);
        Assert.Equal(1.2, curtainArguments.Duration);
        Assert.True(curtainArguments.Wait);
    }

    [Fact(Timeout = 5_000)]
    public async Task CameraPoseCommitsAllPropertiesAsOneOperation()
    {
        var backend = new FakeStageBackend();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IFrameClock>(_ => new ImmediateFrameClock())
            .AddSingleton<IOperationScheduler>(
                resolver => new OperationScheduler(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IStageBackend>(_ => backend)
            .AddSingleton<IStageAccessibility>(_ => new StageAccessibility())
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler),
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateOperationKernelAsync()
    {
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "OperationRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
            ConfigureServices = services =>
                services.AddSingleton<ShutdownStartsOperationService>(
                    resolver => new ShutdownStartsOperationService(
                        resolver.Get<IOperationScheduler>()),
                    typeof(IOperationScheduler)),
        };
        var tweenTarget = new Node2D { Name = "TweenTarget" };
        AddChild(tweenTarget);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var scheduler = bootstrap.Host!.Services.Get<IOperationScheduler>();
        var shutdownStarter = bootstrap.Host.Services.Get<ShutdownStartsOperationService>();
        var advancedFrames = 0;
        var frameOperation = scheduler.Start(new OperationDefinition(
            async context =>
            {
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
            },
            id: "real-frames"));

        Assert(!frameOperation.Completion.IsCompleted,
            "Real-frame operation completed before Godot advanced a frame.");
        await frameOperation.Completion;
        Assert(advancedFrames == 2, "Operation did not advance across two real Godot frames.");

        tweenTarget.Position = Vector2.Zero;
        var naturalTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(80, 20),
            0.03,
            id: "natural-tween"));
        await naturalTween.Completion;
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(80, 20)),
            "Naturally completed Tween did not reach its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var cancelledTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(100, 0),
            1,
            id: "cancelled-tween"));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await scheduler.CancelAsync(OperationTarget.ForId("cancelled-tween"));
        Assert(cancelledTween.State == OperationState.Cancelled,
            "Cancelled Tween did not enter Cancelled state.");
        Assert(!tweenTarget.Position.IsEqualApprox(new Vector2(100, 0)),
            "Cancelled Tween incorrectly applied its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var completedTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(120, 40),
            1,
            id: "completed-tween"));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await scheduler.CompleteAsync(OperationTarget.ForId("completed-tween"));
        Assert(completedTween.State == OperationState.Completed,
            "Force-completed Tween did not enter Completed state.");
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(120, 40)),
            "Force-completed Tween did not apply its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var shutdownTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(200, 0),
            10,
            id: "shutdown-tween"));
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Count == 0,
            2);

        Assert(shutdownTween.State == OperationState.Cancelled,
            "Operation active during Runtime exit was not cancelled.");
        Assert(shutdownStarter.StartedOperation.State == OperationState.Cancelled,
            "Operation started during Runtime exit was not cancelled.");
        Assert(!tweenTarget.Position.IsEqualApprox(new Vector2(200, 0)),
            "Runtime-exit cancellation incorrectly completed the Tween.");

        tweenTarget.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap), "Operation Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(tweenTarget), "Tween test node leaked after cleanup.");
    }

    private sealed class ShutdownStartsOperationService : IRuntimeService
    {
        private readonly IOperationScheduler scheduler;

        public ShutdownStartsOperationService(IOperationScheduler scheduler)
        {
            this.scheduler = scheduler;
        }

        public IOperationHandle StartedOperation { get; private set; } = null!;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            StartedOperation = scheduler.Start(new OperationDefinition(
                context => context.FrameClock.NextFrameAsync(context.CancellationToken),
                id: "started-during-shutdown"));
            try
            {
                await StartedOperation.Completion;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

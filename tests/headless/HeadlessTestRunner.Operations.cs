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
                services.AddSingleton(
                    resolver => new ShutdownStartsOperationService(
                        resolver.Get<IOperationRunner>()),
                    typeof(IOperationRunner)),
        };
        var tweenTarget = new Node2D { Name = "TweenTarget" };
        AddChild(tweenTarget);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var scheduler = bootstrap.Host!.Services.Get<IOperationRunner>();
        var activities = bootstrap.Host.Services.Get<IRuntimeActivityObserver>();
        var shutdownStarter = bootstrap.Host.Services.Get<ShutdownStartsOperationService>();
        var advancedFrames = 0;
        var frameOperation = scheduler.Start(new OperationDefinition(
            async context =>
            {
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
            }));

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
            0.03));
        await naturalTween.Completion;
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(80, 20)),
            "Naturally completed Tween did not reach its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var cancelledTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(100, 0),
            1));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        cancelledTween.Cancel();
        try { await cancelledTween.Completion; } catch (OperationCanceledException) { }
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
            1));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        completedTween.Finish();
        await completedTween.Completion;
        Assert(completedTween.State == OperationState.Completed,
            "Force-completed Tween did not enter Completed state.");
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(120, 40)),
            "Force-completed Tween did not apply its final property value.");

        var methodTweenValue = 0.0f;
        var naturalMethodTween = scheduler.Start(GodotTweenOperation.CreateFloatMethodTween(
            this,
            () => methodTweenValue,
            value => methodTweenValue = value,
            1,
            0.03));
        await naturalMethodTween.Completion;
        Assert(Mathf.IsEqualApprox(methodTweenValue, 1),
            "Naturally completed method Tween did not apply its exact final value.");

        methodTweenValue = 0;
        var cancelledMethodTween = scheduler.Start(GodotTweenOperation.CreateFloatMethodTween(
            this,
            () => methodTweenValue,
            value => methodTweenValue = value,
            1,
            1));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        cancelledMethodTween.Cancel();
        try { await cancelledMethodTween.Completion; } catch (OperationCanceledException) { }
        Assert(cancelledMethodTween.State == OperationState.Cancelled &&
                methodTweenValue < 1,
            "Cancelled method Tween incorrectly applied its final value.");

        methodTweenValue = 0;
        var completedMethodTween = scheduler.Start(GodotTweenOperation.CreateFloatMethodTween(
            this,
            () => methodTweenValue,
            value => methodTweenValue = value,
            1,
            1));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        completedMethodTween.Finish();
        await completedMethodTween.Completion;
        Assert(completedMethodTween.State == OperationState.Completed &&
                Mathf.IsEqualApprox(methodTweenValue, 1),
            "Force-completed method Tween did not apply its final value.");

        tweenTarget.Position = Vector2.Zero;
        var shutdownTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(200, 0),
            10));
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await WaitForConditionAsync(
            () => !activities.HasActiveActivities(RuntimeActivityOwner.Other),
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
        private readonly IOperationRunner scheduler;

        public ShutdownStartsOperationService(IOperationRunner scheduler)
        {
            this.scheduler = scheduler;
        }

        public IOperationHandle StartedOperation { get; private set; } = null!;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            StartedOperation = scheduler.Start(new OperationDefinition(
                context => context.FrameClock.NextFrameAsync(context.CancellationToken)));
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

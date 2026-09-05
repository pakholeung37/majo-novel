#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CameraFxCueHandlerTests
{
    [Fact(Timeout = 5_000)]
    public async Task CameraFxCueHandler_Settlement_Cancel_Finish_Contracts()
    {
        var mockModifiers = new MockCameraModifierManager();
        var context = new TransientCueExecutionContext(mockModifiers);

        var cueRegistry = new TransientCueHandlerRegistry();
        var cue = new CameraFxCue(ViewId.Main, "shake", new Dictionary<string, string>(), 0.5);
        var handler = cueRegistry.Resolve(cue);
        handler.Validate(cue);

        // Execute returns operation handle
        var handle = handler.Execute(cue, context);
        Assert.NotNull(handle);
        Assert.Equal(OperationState.Running, handle.State);

        // Test complete/finish
        handle.Finish();
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);

        // Test cancellation on another instance
        var handle2 = handler.Execute(cue, context);
        handle2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle2.Completion);
        Assert.Equal(OperationState.Cancelled, handle2.State);
    }

    private sealed class MockCameraModifierManager : ICameraModifierManager
    {
        public bool Contains(string effectName) => true;

        public IOperationHandle ApplyEffect(CameraFxCue cue)
        {
            return new MockOperationHandle(cue.DurationSeconds);
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MockOperationHandle : IOperationHandle
    {
        private readonly TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MockOperationHandle(double durationSeconds)
        {
            State = OperationState.Running;
        }

        public OperationState State { get; private set; }
        public Task Completion => tcs.Task;

        public void Cancel()
        {
            if (State is OperationState.Completed or OperationState.Cancelled) return;
            State = OperationState.Cancelled;
            tcs.TrySetCanceled();
        }

        public void Finish()
        {
            if (State is OperationState.Completed or OperationState.Cancelled) return;
            State = OperationState.Completed;
            tcs.TrySetResult();
        }
    }
}

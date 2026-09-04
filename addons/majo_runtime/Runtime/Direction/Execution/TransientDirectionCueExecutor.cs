#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Execution;

public interface ITransientDirectionCueExecutor : IRuntimeService
{
    void Validate(IEnumerable<ITransientDirectionCue> cues);

    Task<ITransientCueActivity?> StartCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken);

    Task IRuntimeService.InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task IRuntimeService.ResetAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task IRuntimeService.ShutdownAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class TransientDirectionCueExecutor : ITransientDirectionCueExecutor
{
    private readonly ITransientCueHandlerRegistry handlers;
    private readonly TransientCueExecutionContext context;
    private readonly IRuntimeActivityRegistry? activities;

    public TransientDirectionCueExecutor(
        ITransientCueHandlerRegistry handlers,
        ICameraModifierManager modifiers,
        IServiceResolver? services = null,
        IRuntimeActivityRegistry? activities = null)
    {
        this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        context = new TransientCueExecutionContext(modifiers, services);
        this.activities = activities;
    }

    public void Validate(IEnumerable<ITransientDirectionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        foreach (var cue in cues)
        {
            var handler = handlers.Resolve(cue);
            handler.Validate(cue);
        }
    }

    public Task<ITransientCueActivity?> StartCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var capturedCues = cues.ToArray();
        if (capturedCues.Length == 0)
        {
            return Task.FromResult<ITransientCueActivity?>(null);
        }

        var operations = new List<IOperationHandle>();
        IDisposable? activityLease = null;
        try
        {
            foreach (var cue in capturedCues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handler = handlers.Resolve(cue);
                handler.Validate(cue);
                var handle = handler.Execute(cue, context);
                operations.Add(handle);
            }

            activityLease = activities?.BeginActivity(
                RuntimeActivityOwner.Direction,
                "TransientCue",
                token.TransactionId.Value.ToString(),
                isCancelable: true,
                isFinishable: true);

            ITransientCueActivity activity = new TransientCueActivity(
                operations,
                isSkippable: true,
                activityLease);
            return Task.FromResult<ITransientCueActivity?>(activity);
        }
        catch
        {
            activityLease?.Dispose();
            foreach (var operation in operations)
            {
                try
                {
                    operation.Cancel();
                }
                catch
                {
                    // Ignore error during cancellation cleanup on start fault
                }
            }
            throw;
        }
    }
}

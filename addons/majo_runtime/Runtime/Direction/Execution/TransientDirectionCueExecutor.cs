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

public interface ITransientDirectionCueExecutor
{
    void Validate(IEnumerable<ITransientDirectionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
    }

    Task ExecuteAsync(IEnumerable<ITransientDirectionCue> cues,
        DirectionCompletionBarrier completion, CancellationToken cancellationToken);

    Task ExecuteCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(cues, completion, cancellationToken);
}

public sealed class TransientDirectionCueExecutor : ITransientDirectionCueExecutor
{
    private readonly ITransientCueHandlerRegistry handlers;
    private readonly TransientCueExecutionContext context;

    public TransientDirectionCueExecutor(
        ITransientCueHandlerRegistry handlers,
        ICameraModifierManager modifiers,
        IScreenEffectManager effects,
        IStageCurtainManager curtain,
        IServiceResolver? services = null)
    {
        this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        context = new TransientCueExecutionContext(modifiers, effects, curtain, services);
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

    public async Task ExecuteAsync(IEnumerable<ITransientDirectionCue> cues,
        DirectionCompletionBarrier completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (!Enum.IsDefined(completion))
            throw new ArgumentOutOfRangeException(nameof(completion));
        var operations = new List<IOperationHandle>();
        try
        {
            foreach (var cue in cues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handler = handlers.Resolve(cue);
                handler.Validate(cue);
                operations.Add(handler.Execute(cue, context));
            }
            if (completion == DirectionCompletionBarrier.Settle)
                await Task.WhenAll(operations.Select(operation => operation.Completion)).WaitAsync(cancellationToken);
        }
        catch
        {
            foreach (var operation in operations)
                operation.Cancel();
            throw;
        }
    }

}

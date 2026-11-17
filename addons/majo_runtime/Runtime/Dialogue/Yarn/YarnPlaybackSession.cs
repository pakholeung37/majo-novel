#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Yarn;

internal enum YarnPlaybackSessionPhase { Idle, Running, Stopping, Completed, Faulted }

// The public completion belongs to a story session, not to an individual Runner replay.
internal sealed class YarnPlaybackSession
{
    private readonly object gate = new();
    private TaskCompletionSource<DialogueCompletion> completion = NewCompletion();
    private int replayDepth;
    private int nextLineRequested;
    private bool progressionFrozen;
    private Exception? failure;

    public YarnPlaybackSession() => completion.SetResult(
        new DialogueCompletion(DialogueCompletionKind.Completed, string.Empty, string.Empty));

    public string ProjectId { get; private set; } = string.Empty;
    public string StartNode { get; private set; } = string.Empty;
    public YarnPlaybackSessionPhase Phase { get; private set; }
    public bool IsStopping => Phase == YarnPlaybackSessionPhase.Stopping;
    public bool IsReplaying { get { lock (gate) return replayDepth != 0; } }
    public bool ProgressionFrozen { get { lock (gate) return progressionFrozen; } }
    public Exception? Failure { get { lock (gate) return failure; } }
    public Task<DialogueCompletion> Completion { get { lock (gate) return completion.Task; } }

    public void Begin(string projectId, string startNode, bool resetCompletion)
    {
        lock (gate)
        {
            ProjectId = projectId;
            StartNode = startNode;
            failure = null;
            Phase = YarnPlaybackSessionPhase.Running;
            if (resetCompletion) completion = NewCompletion();
        }
        ResetAdvance();
    }

    public void RequestStop()
    {
        lock (gate)
            if (!completion.Task.IsCompleted) Phase = YarnPlaybackSessionPhase.Stopping;
    }
    public void RecordFailure(Exception exception) { lock (gate) failure = exception; }
    public void ClearFailure() { lock (gate) failure = null; }
    public void SetProgressionFrozen(bool frozen) { lock (gate) progressionFrozen = frozen; }
    public bool TryRequestAdvance() => !ProgressionFrozen && Interlocked.Exchange(ref nextLineRequested, 1) == 0;
    public void ResetAdvance() => Volatile.Write(ref nextLineRequested, 0);

    public void Complete(DialogueCompletionKind kind)
    {
        lock (gate)
        {
            if (replayDepth != 0) return;
            if (completion.TrySetResult(new DialogueCompletion(kind, ProjectId, StartNode)))
                Phase = YarnPlaybackSessionPhase.Completed;
        }
    }
    public void Fault()
    {
        lock (gate)
        {
            if (replayDepth != 0 || failure is null) return;
            if (completion.TrySetException(failure)) Phase = YarnPlaybackSessionPhase.Faulted;
            _ = completion.Task.Exception;
        }
    }

    public IDisposable EnterReplay()
    {
        lock (gate) replayDepth++;
        return new ReplayLease(this);
    }

    private static TaskCompletionSource<DialogueCompletion> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ReplayLease(YarnPlaybackSession owner) : IDisposable
    {
        private YarnPlaybackSession? session = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref session, null);
            if (current is not null) lock (current.gate) current.replayDepth--;
        }
    }
}

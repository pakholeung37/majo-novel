using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class YarnPlaybackSessionTests
{
    [Fact]
    public async Task NestedReplayKeepsPublicCompletionUntilTheActualSessionEnds()
    {
        var session = new YarnPlaybackSession();
        session.Begin("main", "Start", resetCompletion: true);
        var completion = session.Completion;
        var outer = session.EnterReplay();
        using (session.EnterReplay())
        {
            session.RequestStop();
            session.Complete(DialogueCompletionKind.Stopped);
            session.Begin("main", "Other", resetCompletion: false);
        }
        Assert.True(session.IsReplaying);
        Assert.Same(completion, session.Completion);
        Assert.False(completion.IsCompleted);
        outer.Dispose();
        outer.Dispose();
        Assert.False(session.IsReplaying);

        session.Complete(DialogueCompletionKind.Completed);
        var result = await completion;
        Assert.Equal("Other", result.StartNode);
        Assert.Equal(DialogueCompletionKind.Completed, result.Kind);
    }

    [Fact]
    public async Task FailureBelongsToTheOldSessionAndDoesNotPoisonARestart()
    {
        var session = new YarnPlaybackSession();
        session.Begin("main", "Start", resetCompletion: true);
        var old = session.Completion;
        var failure = new InvalidOperationException("command failed");
        session.RecordFailure(failure);
        session.Fault();
        session.Complete(DialogueCompletionKind.Stopped);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => old));
        Assert.Equal(YarnPlaybackSessionPhase.Faulted, session.Phase);

        session.Begin("next", "Start", resetCompletion: true);
        Assert.Null(session.Failure);
        Assert.NotSame(old, session.Completion);
        Assert.False(session.Completion.IsCompleted);
        session.RequestStop();
        session.Complete(DialogueCompletionKind.Stopped);
        Assert.Equal(DialogueCompletionKind.Stopped, (await session.Completion).Kind);
    }

    [Fact]
    public void FailedReplayScopeIsReleasedWithoutCompletingTheStorySession()
    {
        var session = new YarnPlaybackSession();
        session.Begin("main", "Start", resetCompletion: true);
        Assert.Throws<InvalidOperationException>(new Action(() =>
        {
            using var replay = session.EnterReplay();
            session.RecordFailure(new InvalidOperationException("replay failed"));
            session.Fault();
            Assert.False(session.Completion.IsCompleted);
            throw new InvalidOperationException("prepare failed");
        }));
        Assert.False(session.IsReplaying);
        session.Fault();
        Assert.True(session.Completion.IsFaulted);
    }

    [Fact]
    public void FreezeAndLineBoundariesControlAdvanceWithoutLosingThePublicSession()
    {
        var session = new YarnPlaybackSession();
        session.Begin("main", "Start", resetCompletion: true);
        session.SetProgressionFrozen(true);
        Assert.False(session.TryRequestAdvance());
        session.SetProgressionFrozen(false);
        Assert.True(session.TryRequestAdvance());
        Assert.False(session.TryRequestAdvance());
        session.ResetAdvance();
        Assert.True(session.TryRequestAdvance());
        Assert.False(session.Completion.IsCompleted);
    }
}

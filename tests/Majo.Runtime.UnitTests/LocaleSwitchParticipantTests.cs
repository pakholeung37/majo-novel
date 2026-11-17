using Majo.Runtime.Localization;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LocaleSwitchParticipantTests
{
    [Fact]
    public async Task FailedBoundaryReportsFailureWithoutApplyingAndAllowsRetry()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        var fail = true;
        var participant = new Participant
        {
            Busy = true,
            Wait = _ => fail ? Task.FromException(new InvalidOperationException("boundary failed")) : Task.CompletedTask,
        };
        using var registration = manager.RegisterDialogueSwitchParticipant(participant);
        var failures = new List<LocaleSwitchFailedEvent>();
        manager.SwitchFailed += failures.Add;

        Assert.False((await manager.ChangeDialogueLocaleAsync("ja")).Success);

        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
        Assert.Empty(participant.Applied);
        Assert.Single(failures);
        fail = false;
        Assert.True((await manager.ChangeDialogueLocaleAsync("ja")).Success);
    }

    [Fact]
    public async Task CancellationAtBoundaryCannotCommitAnUncooperativeParticipant()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        using var cancellation = new CancellationTokenSource();
        var participant = new Participant
        {
            Busy = true,
            Wait = _ => { cancellation.Cancel(); return Task.CompletedTask; },
        };
        using var registration = manager.RegisterDialogueSwitchParticipant(participant);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ChangeDialogueLocaleAsync("ja", cancellation.Token));

        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
        Assert.Empty(participant.Applied);
    }

    [Fact]
    public async Task ThrowingObserversCannotAbortSwitchOrHideItFromLaterObservers()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        var participant = new Participant();
        using var registration = manager.RegisterDialogueSwitchParticipant(participant);
        var observed = new List<string>();
        manager.SwitchStarting += _ => throw new InvalidOperationException("observer failed");
        manager.DialogueLocaleChanged += _ => throw new InvalidOperationException("observer failed");
        manager.DialogueLocaleChanged += change => observed.Add(change.CurrentLocale);
        manager.SwitchFailed += _ => throw new InvalidOperationException("observer failed");

        Assert.True((await manager.ChangeDialogueLocaleAsync("ja")).Success);
        Assert.Equal(["ja"], observed);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
        participant.FailNext = true;
        Assert.False((await manager.ChangeDialogueLocaleAsync("en")).Success);
        Assert.Equal("ja", manager.DialogueLocale);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
    }

    [Fact]
    public async Task SwitchWaitsForParticipantBoundaryBeforeApplyingAndPublishing()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new Participant { Busy = true, Wait = token => ready.Task.WaitAsync(token) };
        using var registration = manager.RegisterDialogueSwitchParticipant(participant);
        var published = false;
        manager.DialogueLocaleChanged += _ => published = true;
        var change = manager.ChangeDialogueLocaleAsync("ja");
        Assert.False(change.IsCompleted);
        Assert.False(published);
        Assert.Empty(participant.Applied);
        ready.SetResult();
        Assert.True((await change).Success);
        Assert.True(published);
        Assert.Equal(["ja"], participant.Applied);
    }

    [Fact]
    public async Task FailedParticipantCompensatesAllParticipantsAndAllowsLaterSwitch()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        var first = new Participant();
        var second = new Participant { FailNext = true };
        using var a = manager.RegisterDialogueSwitchParticipant(first);
        using var b = manager.RegisterDialogueSwitchParticipant(second);
        Assert.False((await manager.ChangeDialogueLocaleAsync("ja")).Success);
        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(["ja", "en"], first.Applied);
        Assert.Equal(["ja", "en"], second.Applied);
        Assert.True((await manager.ChangeDialogueLocaleAsync("ja")).Success);
    }

    [Fact]
    public async Task CancelledBoundaryDoesNotApplyAndDisposedRegistrationIsDetached()
    {
        using var manager = new LocaleManager("en", ["en", "ja"]);
        var participant = new Participant { Busy = true, Wait = token => Task.Delay(Timeout.Infinite, token) };
        var registration = manager.RegisterDialogueSwitchParticipant(participant);
        using var cancellation = new CancellationTokenSource();
        var change = manager.ChangeDialogueLocaleAsync("ja", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        Assert.Empty(participant.Applied);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
        registration.Dispose();
        registration.Dispose();
        Assert.True((await manager.ChangeDialogueLocaleAsync("ja")).Success);
        Assert.Empty(participant.Applied);
    }

    private sealed class Participant : IDialogueLocaleSwitchParticipant
    {
        public bool Busy { get; init; }
        public bool IsBusy => Busy;
        public bool FailNext { get; set; }
        public Func<CancellationToken, Task> Wait { get; init; } = _ => Task.CompletedTask;
        public List<string> Applied { get; } = [];
        public Task WaitForSafeBoundaryAsync(CancellationToken cancellationToken) => Wait(cancellationToken);
        public Task ApplyAsync(string locale, CancellationToken cancellationToken)
        {
            Applied.Add(locale);
            if (FailNext) { FailNext = false; throw new InvalidOperationException("presentation failed"); }
            return Task.CompletedTask;
        }
    }
}

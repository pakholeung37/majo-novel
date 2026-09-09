using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class AutoHistoryTests
{
    [Fact(Timeout = 5_000)]
    public async Task AutoWaitsForSafeBoundaryVoiceAndConfiguredDelay()
    {
        var harness = await AutoHarness.CreateAsync();
        var voice = harness.Audio.StartVoice("voiced-line");
        harness.Playback.SetMode(PlaybackMode.Auto);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);

        Assert.Equal(0, harness.Clock.PendingDelayCount);
        Assert.Equal(0, harness.Dialogue.NextLineRequests);

        voice.Complete();
        await harness.Clock.WaitForPendingDelayAsync();
        Assert.Equal(0, harness.Dialogue.NextLineRequests);

        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();

        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task AutoWithoutVoiceStartsDelayOnlyAtAwaitingAdvance()
    {
        var harness = await AutoHarness.CreateAsync();
        harness.Playback.SetMode(PlaybackMode.Auto);

        Assert.Equal(0, harness.Clock.PendingDelayCount);
        harness.Script.SetPhase(ScriptPlaybackPhase.PresentingLine);
        Assert.Equal(0, harness.Clock.PendingDelayCount);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        await harness.Clock.WaitForPendingDelayAsync();

        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();
        Assert.Equal(1, harness.Dialogue.NextLineRequests);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task PreemptedVoiceSettlesTheCapturedVoiceWait()
    {
        var harness = await AutoHarness.CreateAsync();
        var first = harness.Audio.StartVoice("first");
        harness.Playback.SetMode(PlaybackMode.Auto);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);

        harness.Audio.PreemptVoice("replacement");
        await harness.Clock.WaitForPendingDelayAsync();

        Assert.Equal(AudioPlaybackState.Finished, first.State);
        Assert.Equal(0, harness.Dialogue.NextLineRequests);
        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task HistoryPausesAutoAndClosingRestartsAFullDeadline()
    {
        var harness = await AutoHarness.CreateAsync();
        harness.Playback.SetMode(PlaybackMode.Auto);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        await harness.Clock.WaitForPendingDelayAsync();

        harness.History.Open();
        Assert.True(harness.Playback.IsPaused);
        Assert.Equal(0, harness.Clock.PendingDelayCount);

        harness.History.Close();
        Assert.False(harness.Playback.IsPaused);
        await harness.Clock.WaitForPendingDelayAsync();

        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task OptionsPauseAutoAndReturningToLineRestartsAFullDeadline()
    {
        var harness = await AutoHarness.CreateAsync();
        harness.Playback.SetMode(PlaybackMode.Auto);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        await harness.Clock.WaitForPendingDelayAsync();

        harness.Script.SetPhase(ScriptPlaybackPhase.PresentingOptions);
        Assert.True(harness.Playback.IsPaused);
        Assert.Equal(0, harness.Clock.PendingDelayCount);

        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        Assert.False(harness.Playback.IsPaused);
        await harness.Clock.WaitForPendingDelayAsync();
        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();

        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ManualAdvanceWinningDeadlineRaceOnlyRequestsOnce()
    {
        var harness = await AutoHarness.CreateAsync();
        using var manual = harness.Input.AcquireScope(
            new object(),
            new InputScopeOptions([MajoInputActions.Advance]));
        manual.Activated += _ => harness.Dialogue.RequestNextLine();
        harness.Playback.SetMode(PlaybackMode.Auto);
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        await harness.Clock.WaitForPendingDelayAsync();

        harness.InputBackend.Activate(MajoInputActions.Advance, 1);
        harness.Clock.CompleteAllDelays();
        await Task.Yield();

        Assert.Equal(1, harness.Dialogue.NextLineRequests);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task AutoInputTogglesModeAndResetReturnsManual()
    {
        var harness = await AutoHarness.CreateAsync();

        harness.InputBackend.Activate(MajoInputActions.Auto, 1);
        Assert.Equal(PlaybackMode.Auto, harness.Playback.Mode);
        harness.InputBackend.Activate(MajoInputActions.Auto, 2);
        Assert.Equal(PlaybackMode.Manual, harness.Playback.Mode);
        harness.Playback.SetMode(PlaybackMode.Auto);

        await harness.Playback.ResetAsync(CancellationToken.None);

        Assert.Equal(PlaybackMode.Manual, harness.Playback.Mode);
        Assert.False(harness.Playback.IsPaused);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ReadOnlySkipUsesPlayedStateCapturedBeforePresentation()
    {
        var harness = await AutoHarness.CreateAsync();
        var playedLine = Line("line:played", "Played");
        harness.Dialogue.EmitLine(playedLine);
        Assert.False(harness.Played.CurrentPresentation!.WasPlayedBeforePresentation);
        harness.Presentation.CompleteLine();

        harness.Dialogue.EmitLine(playedLine);
        Assert.True(harness.Played.CurrentPresentation!.WasPlayedBeforePresentation);
        harness.Playback.SetMode(PlaybackMode.SkipReadOnly);
        Assert.Equal(PlaybackMode.SkipReadOnly, harness.Playback.Mode);
        harness.Presentation.CompleteLine();
        harness.Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
        await harness.Clock.WaitForPendingDelayAsync();
        harness.Clock.CompleteNextDelay();
        await harness.Dialogue.WaitForNextLineRequestAsync();

        harness.Dialogue.EmitLine(Line("line:new", "New"));
        Assert.False(harness.Played.CurrentPresentation!.WasPlayedBeforePresentation);
        Assert.Equal(PlaybackMode.Manual, harness.Playback.Mode);
        harness.Presentation.CompleteLine();
        Assert.True(harness.Played.IsPlayed("test", "line:new"));
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task SkipAllHurriesCurrentAndEverySubsequentLine()
    {
        var harness = await AutoHarness.CreateAsync();

        harness.Audio.StartVoice("first-voice");
        harness.Dialogue.EmitLine(Line(
            "line:first",
            "First"));
        harness.Playback.SetMode(PlaybackMode.SkipAll);
        Assert.Equal(1, harness.Dialogue.HurryUpRequests);
        Assert.Null(harness.Audio.CurrentVoice);
        Assert.Equal(1, harness.Direction.FinishSkippableCallCount);

        harness.Dialogue.EmitLine(Line(
            "line:second",
            "Second"));
        Assert.Equal(2, harness.Dialogue.HurryUpRequests);
        Assert.Equal(2, harness.Direction.FinishSkippableCallCount);

        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task SameFrameToggleSkipAndManualDoesNotDuplicateFinish()
    {
        var harness = await AutoHarness.CreateAsync();

        harness.Audio.StartVoice("test-voice-1");
        harness.Playback.SetMode(PlaybackMode.SkipAll);
        Assert.Equal(1, harness.Direction.FinishSkippableCallCount);

        harness.Playback.SetMode(PlaybackMode.Manual);
        Assert.Equal(1, harness.Direction.FinishSkippableCallCount);

        harness.Playback.SetMode(PlaybackMode.Manual);
        Assert.Equal(1, harness.Direction.FinishSkippableCallCount);

        await harness.ShutdownAsync();
    }

    [Fact]
    public async Task PlayedRegisterRoundTripsLocaleAndFingerprintPolicy()
    {
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var locale = "zh-Hans";
        var first = new PlayedRegister(
            dialogue,
            presentation,
            PlayedLocalePolicy.PerLocale,
            PlayedContentChangePolicy.TreatAsUnread,
            () => locale);
        await first.InitializeAsync(default);
        dialogue.EmitLine(Line("line:1", "Text", "majo:fingerprint=v1"));
        presentation.CompleteLine();
        Assert.True(first.IsPlayed("test", "line:1", "zh-Hans", "v1"));
        Assert.False(first.IsPlayed("test", "line:1", "en", "v1"));
        Assert.False(first.IsPlayed("test", "line:1", "zh-Hans", "v2"));

        var record = await ((IStateContributor)first).CaptureAsync(default);
        var second = new PlayedRegister(
            dialogue,
            presentation,
            PlayedLocalePolicy.PerLocale,
            PlayedContentChangePolicy.TreatAsUnread,
            () => locale);
        await ((IStateContributor)second).PreflightAsync(record, default);
        await ((IImmediateStateContributor)second).RestoreAsync(record, default);

        Assert.True(second.IsPlayed("test", "line:1", "zh-Hans", "v1"));
        Assert.Equal(first.Entries, second.Entries);
        await first.ShutdownAsync(default);
    }

    [Fact(Timeout = 5_000)]
    public async Task HistoryRecordsCompletedFinalLinesWithCapacityAndVoiceReplay()
    {
        var inputBackend = new FakeInputBackend();
        var input = new InputManager(inputBackend);
        await inputBackend.InitializeAsync(CancellationToken.None);
        await input.InitializeAsync(CancellationToken.None);
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var audio = new FakeAudioManager();
        audio.VoiceClips.Register("en/voice-2");
        var history = new HistoryManager(dialogue, presentation, audio, input, 2);
        await history.InitializeAsync(CancellationToken.None);

        dialogue.EmitLine(Line("same", "First", "voice:none"));
        Assert.Empty(history.Entries);
        presentation.CompleteLine();
        dialogue.EmitLine(Line("same", "Second", "voice:voice-2"));
        presentation.CompleteLine();
        dialogue.EmitLine(Line("third", "Final visible text", "voice:none"));
        presentation.CompleteLine();

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(["same", "third"], history.Entries.Select(entry => entry.LineId));
        Assert.Equal("Final visible text", history.Entries[^1].Text);
        Assert.Equal("voice-2", history.Entries[0].VoiceId);
        Assert.Null(history.Entries[1].VoiceId);
        Assert.True(await history.ReplayVoiceAsync(history.Entries[0].Sequence));
        Assert.Equal("voice-2", audio.LastPlayedVoiceId);
        Assert.False(await history.ReplayVoiceAsync(history.Entries[1].Sequence));

        await history.ResetAsync(CancellationToken.None);
        Assert.Empty(history.Entries);
        await history.ShutdownAsync(CancellationToken.None);
        await input.ShutdownAsync(CancellationToken.None);
        await inputBackend.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task OpenHistoryOwnsModalInputUntilHistoryOrCancelClosesIt()
    {
        var inputBackend = new FakeInputBackend();
        var input = new InputManager(inputBackend);
        await inputBackend.InitializeAsync(CancellationToken.None);
        await input.InitializeAsync(CancellationToken.None);
        var history = new HistoryManager(
            new FakeDialoguePlayer(),
            new FakeDialoguePresentation(),
            new FakeAudioManager(),
            input,
            10);
        await history.InitializeAsync(CancellationToken.None);
        var advanceCount = 0;
        using var dialogueInput = input.AcquireScope(
            new object(),
            new InputScopeOptions([MajoInputActions.Advance]));
        dialogueInput.Activated += _ => advanceCount++;

        inputBackend.Activate(MajoInputActions.History, 1);
        Assert.True(history.IsOpen);
        inputBackend.Activate(MajoInputActions.Advance, 2);
        Assert.Equal(0, advanceCount);
        inputBackend.Activate(MajoInputActions.Cancel, 3);
        Assert.False(history.IsOpen);
        inputBackend.Activate(MajoInputActions.Advance, 4);
        Assert.Equal(1, advanceCount);

        await history.ShutdownAsync(CancellationToken.None);
        await input.ShutdownAsync(CancellationToken.None);
        await inputBackend.ShutdownAsync(CancellationToken.None);
    }

    private static DialogueLine Line(string id, string text, params string[] metadata) => new(
        id,
        "Narrator",
        new DialogueText(text, []),
        metadata);

    private sealed class AutoHarness
    {
        private AutoHarness(
            FakeInputBackend inputBackend,
            InputManager input,
            FakeScriptPlayback script,
            FakeDialoguePlayer dialogue,
            FakeAudioManager audio,
            FakeFrameClock clock,
            FakeHistoryManager history,
            FakeDialoguePresentation presentation,
            PlayedRegister played,
            RecordingDirectionCoordinator direction,
            PlaybackModeManager playback)
        {
            InputBackend = inputBackend;
            Input = input;
            Script = script;
            Dialogue = dialogue;
            Audio = audio;
            Clock = clock;
            History = history;
            Presentation = presentation;
            Played = played;
            Direction = direction;
            Playback = playback;
        }

        public FakeInputBackend InputBackend { get; }
        public InputManager Input { get; }
        public FakeScriptPlayback Script { get; }
        public FakeDialoguePlayer Dialogue { get; }
        public FakeAudioManager Audio { get; }
        public FakeFrameClock Clock { get; }
        public FakeHistoryManager History { get; }
        public FakeDialoguePresentation Presentation { get; }
        public PlayedRegister Played { get; }
        public RecordingDirectionCoordinator Direction { get; }
        public PlaybackModeManager Playback { get; }

        public static async Task<AutoHarness> CreateAsync()
        {
            var inputBackend = new FakeInputBackend();
            var input = new InputManager(inputBackend);
            await inputBackend.InitializeAsync(CancellationToken.None);
            await input.InitializeAsync(CancellationToken.None);
            var script = new FakeScriptPlayback();
            var dialogue = new FakeDialoguePlayer();
            var audio = new FakeAudioManager();
            var clock = new FakeFrameClock();
            var history = new FakeHistoryManager();
            var presentation = new FakeDialoguePresentation();
            var played = new PlayedRegister(dialogue, presentation);
            var direction = new RecordingDirectionCoordinator();
            var playback = new PlaybackModeManager(
                script,
                dialogue,
                audio,
                clock,
                input,
                history,
                played,
                direction,
                TimeSpan.FromSeconds(1),
                waitForVoice: true,
                skipDelay: TimeSpan.FromMilliseconds(10));
            await played.InitializeAsync(CancellationToken.None);
            await playback.InitializeAsync(CancellationToken.None);
            return new AutoHarness(
                inputBackend,
                input,
                script,
                dialogue,
                audio,
                clock,
                history,
                presentation,
                played,
                direction,
                playback);
        }

        public async Task ShutdownAsync()
        {
            await Playback.ShutdownAsync(CancellationToken.None);
            await Played.ShutdownAsync(CancellationToken.None);
            await Input.ShutdownAsync(CancellationToken.None);
            await InputBackend.ShutdownAsync(CancellationToken.None);
        }
    }

    private sealed class FakeFrameClock : IFrameClock
    {
        private readonly object sync = new();
        private readonly List<PendingDelay> delays = new();
        private TaskCompletionSource delayScheduled = NewSignal();

        public int PendingDelayCount
        {
            get { lock (sync) { return delays.Count; } }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NextFrameAsync(CancellationToken cancellationToken = default) =>
            DelayAsync(TimeSpan.Zero, cancellationToken);
        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            DelayAsync(duration, FrameTimeMode.Scaled, cancellationToken);

        public Task DelayAsync(
            TimeSpan duration,
            FrameTimeMode timeMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delay = new PendingDelay(completion);
            delay.Registration = cancellationToken.Register(() =>
            {
                lock (sync)
                {
                    delays.Remove(delay);
                }

                completion.TrySetCanceled(cancellationToken);
            });
            TaskCompletionSource scheduled;
            lock (sync)
            {
                delays.Add(delay);
                scheduled = delayScheduled;
                delayScheduled = NewSignal();
            }

            scheduled.TrySetResult();
            return completion.Task;
        }

        public Task WaitForPendingDelayAsync()
        {
            lock (sync)
            {
                return delays.Count > 0 ? Task.CompletedTask : delayScheduled.Task;
            }
        }

        public void CompleteNextDelay()
        {
            PendingDelay delay;
            lock (sync)
            {
                delay = delays[0];
                delays.RemoveAt(0);
            }

            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }

        public void CompleteAllDelays()
        {
            while (PendingDelayCount > 0)
            {
                CompleteNextDelay();
            }
        }

        private static TaskCompletionSource NewSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class PendingDelay(TaskCompletionSource completion)
        {
            public TaskCompletionSource Completion { get; } = completion;
            public CancellationTokenRegistration Registration { get; set; }
        }
    }


    private sealed class FakeHistoryManager : IHistoryManager
    {
        public event Action<HistoryOpenStateChanged>? OpenStateChanged;
        public event Action<HistoryEntriesChanged>? EntriesChanged { add { } remove { } }
        public IReadOnlyList<HistoryEntry> Entries => [];
        public int Capacity => 10;
        public bool IsOpen { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Open()
        {
            IsOpen = true;
            OpenStateChanged?.Invoke(new HistoryOpenStateChanged(true));
        }
        public void Close()
        {
            IsOpen = false;
            OpenStateChanged?.Invoke(new HistoryOpenStateChanged(false));
        }
        public void Toggle() { if (IsOpen) Close(); else Open(); }
        public void Clear() { }
        public Task<bool> ReplayVoiceAsync(
            long sequence,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }


    private sealed class FakeAudioManager : IAudioManager
    {
        private FakePlayback? voice;
        public event Action<IAudioPlayback>? PlaybackStarted { add { } remove { } }
        public event Action<IAudioPlayback>? PlaybackStopped { add { } remove { } }
        public float MasterVolume { get; set; } = 1;
        public float BgmVolume { get; set; } = 1;
        public float SfxVolume { get; set; } = 1;
        public float VoiceVolume { get; set; } = 1;
        public bool MasterMuted { get; set; }
        public bool BgmMuted { get; set; }
        public bool SfxMuted { get; set; }
        public bool VoiceMuted { get; set; }
        public string VoiceLocale { get; set; } = "en";
        public FakeLoader VoiceClips { get; } = new();
        public IAudioClipLoader BgmLoader => VoiceClips;
        public IAudioClipLoader SfxLoader => VoiceClips;
        public IAudioClipLoader VoiceLoader => VoiceClips;
        public IReadOnlyCollection<IAudioPlayback> BgmTracks => [];
        public IReadOnlyCollection<IAudioPlayback> SfxInstances => [];
        public IAudioPlayback? CurrentVoice => voice;
        public bool IsFading(string instanceId) => false;
        public string? LastPlayedVoiceId { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            voice?.Complete();
            voice = null;
            return Task.CompletedTask;
        }
        public Task ShutdownAsync(CancellationToken cancellationToken) =>
            ResetAsync(cancellationToken);
        public FakePlayback StartVoice(string id)
        {
            voice = new FakePlayback(id);
            return voice;
        }

        public FakePlayback PreemptVoice(string id)
        {
            voice?.Complete();
            return StartVoice(id);
        }
        public Task<AudioPlaybackResult> PlayVoiceAsync(
            string clipId,
            bool loop = false,
            float volume = 1,
            AudioFade fade = default,
            string? overridePath = null,
            CancellationToken cancellationToken = default)
        {
            LastPlayedVoiceId = clipId;
            voice = new FakePlayback(clipId);
            return Task.FromResult(new AudioPlaybackResult(voice, Task.CompletedTask));
        }
        public Task<AudioPlaybackResult> PlayBgmAsync(
            string clipId, string? trackId = null, bool loop = true, float volume = 1,
            AudioFade fade = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StopBgmAsync(
            string? trackId = null, AudioFade fade = default,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AudioPlaybackResult> PlaySfxAsync(
            string clipId, string? instanceId = null, bool loop = false, float volume = 1,
            AudioFade fade = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StopSfxAsync(
            string? instanceId = null, AudioFade fade = default,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopVoiceAsync(
            AudioFade fade = default, CancellationToken cancellationToken = default)
        {
            voice?.Complete();
            voice = null;
            return Task.CompletedTask;
        }
        public Task FinishTransitionsAsync(string? targetId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task CancelTransitionsAsync(string? targetId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePlayback(string id) : IAudioPlayback
    {
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioCategory Category => AudioCategory.Voice;
        public string ClipId => id;
        public string InstanceId => id;
        public bool IsLooping => false;
        public float Volume => 1;
        public AudioPlaybackState State { get; private set; } = AudioPlaybackState.Playing;
        public Task Completion => completion.Task;
        public void Complete()
        {
            State = AudioPlaybackState.Finished;
            completion.TrySetResult();
        }
    }

    private sealed class FakeLoader : IAudioClipLoader
    {
        private readonly HashSet<string> clips = new(StringComparer.Ordinal);
        public string PathPrefix => "fake";
        public FakeLoader Register(string id) { clips.Add(id); return this; }
        public bool Exists(string clipId, string? overridePath = null) =>
            clips.Contains(overridePath ?? clipId);
        public Task<IAudioClip> LoadAsync(
            string clipId, string? overridePath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAudioClip>(new FakeAudioClip(overridePath ?? clipId));
    }

    private sealed class CompletedOperation : IOperationHandle
    {
        public static CompletedOperation Instance { get; } = new();
        public OperationState State => OperationState.Completed;
        public Task Completion => Task.CompletedTask;
        public void Cancel() { }
        public void Finish() { }
    }

    private sealed class RecordingDirectionCoordinator : IDirectionTransactionCoordinator
    {
        public DirectionState State => DirectionState.CreateDefault();
        public int FinishSkippableCallCount { get; private set; }

        public Task<CommittedDirectionTransaction> ExecuteAsync(
            IEnumerable<IDirectionDirective> directives,
            DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CommittedDirectionTransaction> RollbackAsync(
            CommittedDirectionTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinishAsync(
            CommittedDirectionTransaction transaction,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RestoreAsync(
            DirectionState target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinishSkippableAsync(CancellationToken cancellationToken = default)
        {
            FinishSkippableCallCount++;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

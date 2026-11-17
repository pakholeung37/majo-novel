using Majo.Runtime.Actors;
using Majo.Runtime.Localization;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorLifetimeOwnershipTests
{
    private static ActorDefinition Definition() => new("hero", "test",
        new TextureActorDefinitionData("hero", "test", "default", []));

    [Fact(Timeout = 5_000)]
    public async Task ResetDrainsLateFactoryCleanupBeforeOpeningTheNextGeneration()
    {
        var factory = new DelayedFactory();
        using var manager = new ActorManager([Definition()], [factory]);
        await manager.InitializeAsync(default);
        var creation = manager.CreateAsync("hero");
        var reset = manager.ResetAsync(default);
        var actor = new BlockingDestroyActor();
        factory.Result.SetResult(actor);
        var finishedEarly = false;
        try
        {
            await actor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            finishedEarly = await Task.WhenAny(reset, Task.Delay(100)) == reset;
        }
        finally { actor.Allow.TrySetResult(); }
        await actor.Finished.Task;
        await reset;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.False(finishedEarly, "Reset returned while the cancelled factory result was still being destroyed.");
        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(default);
    }

    [Fact]
    public async Task CreationAndCleanupFailuresReachTheSameCaller()
    {
        var actor = new BrokenActor("wrong");
        using var manager = new ActorManager([Definition()], [new ImmediateFactory(actor)]);
        await manager.InitializeAsync(default);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => manager.CreateAsync("hero"));
        Assert.Contains(failure.InnerExceptions, value => value is ActorException);
        Assert.Contains(failure.InnerExceptions, value => value.Message == "destroy failed");
        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(default);
    }

    [Fact]
    public async Task FailedContentShutdownStillDetachesTheLocaleObserver()
    {
        var locales = new CountingLocales();
        using var manager = new ActorManager([Definition()], [new ImmediateFactory(new BrokenActor("hero"))], locales);
        await manager.InitializeAsync(default);
        await manager.CreateAsync("hero");
        Assert.Equal(1, locales.Observers);
        await Assert.ThrowsAsync<AggregateException>(() => manager.ShutdownAsync(default));
        Assert.Equal(0, locales.Observers);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillDestroysContentAndDetachesObservers()
    {
        var actor = new FakeActor("hero");
        var locales = new CountingLocales();
        using var manager = new ActorManager([Definition()], [new ThrowingCancellationFactory(actor)], locales);
        await manager.InitializeAsync(default);
        await manager.CreateAsync("hero");
        var failure = await Assert.ThrowsAsync<AggregateException>(() => manager.ShutdownAsync(default));
        Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "cancel handler failed");
        Assert.True(actor.IsDestroyed);
        Assert.Equal(0, locales.Observers);
    }

    private sealed class ThrowingCancellationFactory(IActor actor) : FakeActorFactory("test")
    {
        public override Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token)
        {
            token.Register(() => throw new InvalidOperationException("cancel handler failed"));
            return Task.FromResult(actor);
        }
    }

    private sealed class DelayedFactory : FakeActorFactory
    {
        public DelayedFactory() : base("test") { }
        public TaskCompletionSource<IActor> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) => Result.Task;
    }
    private sealed class ImmediateFactory(IActor actor) : FakeActorFactory("test")
    {
        public override Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) => Task.FromResult(actor);
    }
    private sealed class BrokenActor(string id) : FakeActor(id)
    {
        public override void Destroy() => throw new InvalidOperationException("destroy failed");
    }
    private sealed class BlockingDestroyActor() : FakeActor("hero")
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Allow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Destroy()
        {
            Entered.TrySetResult();
            if (!Allow.Task.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("test cleanup did not resume");
            base.Destroy();
            Finished.TrySetResult();
        }
    }
    private sealed class CountingLocales : ILocaleManager
    {
        private Action<LocaleChangedEvent>? changed;
        public int Observers => changed?.GetInvocationList().Length ?? 0;
        public event Action<LocaleChangedEvent>? DialogueLocaleChanged { add => changed += value; remove => changed -= value; }
        public event Action<LocaleChangedEvent>? VoiceLocaleChanged { add { } remove { } }
        public event Action<LocaleSwitchStartingEvent>? SwitchStarting { add { } remove { } }
        public event Action<LocaleSwitchFailedEvent>? SwitchFailed { add { } remove { } }
        public string DialogueLocale => "en";
        public string VoiceLocale => "en";
        public string DefaultLocale => "en";
        public string DefaultVoiceLocale => "en";
        public IReadOnlyList<string> AvailableLocales => ["en"];
        public LocaleSwitchState SwitchState => LocaleSwitchState.Idle;
        public bool IsInitialized => true;
        public IFontCatalog Fonts => throw new NotSupportedException();
        public IReadOnlyList<string> GetDialogueFallbackChain(string locale) => [locale];
        public IReadOnlyList<string> GetVoiceFallbackChain(string locale) => [locale];
        public IDisposable RegisterDialogueSwitchParticipant(IDialogueLocaleSwitchParticipant participant) => throw new NotSupportedException();
        public Task<LocaleSwitchResult> ChangeDialogueLocaleAsync(string locale, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocaleSwitchResult> ChangeVoiceLocaleAsync(string locale, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public LocaleDiagnosticsSnapshot CaptureDiagnostics() => throw new NotSupportedException();
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

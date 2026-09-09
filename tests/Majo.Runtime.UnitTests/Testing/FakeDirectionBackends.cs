#nullable enable

using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class ExpectedDirectionException : Exception;

public sealed class FakeDirectionBackend : IDirectionPreparationBackend
{
    public Exception? PrepareException { get; init; }
    public Exception? SettlementException { get; init; }
    public Exception? FinishException { get; init; }
    public Exception? DisposeException { get; init; }
    public bool AutoSettle { get; init; }
    public int CommitCount { get; private set; }
    public int FinishCount { get; private set; }
    public int RollbackCount { get; private set; }
    public int DisposeCount { get; private set; }
    public DirectionState? VisibleState { get; private set; }
    public DirectionCommitToken? LastToken { get; private set; }
    public TaskCompletionSource? LastSettlementTcs { get; private set; }

    public Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionExecutionPlan plan,
        CancellationToken cancellationToken) =>
        PrepareAsync(plan.DomainPlan, cancellationToken);

    public Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PrepareException is not null) throw PrepareException;
        var prep = new Prepared(this, plan);
        LastSettlementTcs = prep.SettlementTcs;
        return Task.FromResult<IPreparedDirectionTransaction>(prep);
    }

    public sealed class Prepared : IPreparedDirectionTransaction
    {
        private readonly FakeDirectionBackend owner;
        private readonly DirectionPlan plan;
        public TaskCompletionSource SettlementTcs { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool committed;

        public Prepared(FakeDirectionBackend owner, DirectionPlan plan)
        {
            this.owner = owner;
            this.plan = plan;
        }

        public Task Settlement => SettlementTcs.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("Already committed.");
            committed = true;
            owner.CommitCount++;
            owner.LastToken = token;
            owner.VisibleState = plan.TargetState;
            if (owner.SettlementException is not null)
                SettlementTcs.TrySetException(owner.SettlementException);
            else if (owner.AutoSettle) SettlementTcs.TrySetResult();
        }

        public void Rollback(DirectionCommitToken token)
        {
            owner.RollbackCount++;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed) throw new InvalidOperationException("Not committed.");
            owner.FinishCount++;
            if (owner.FinishException is not null)
            {
                SettlementTcs.TrySetException(owner.FinishException);
                return ValueTask.FromException(owner.FinishException);
            }
            SettlementTcs.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.DisposeCount++;
            if (!committed) SettlementTcs.TrySetCanceled();
            if (owner.DisposeException is not null)
                return ValueTask.FromException(owner.DisposeException);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ComponentBackendHarness :
    IDirectionStageBackend,
    IDirectionViewBackend,
    IDirectionScreenBackend,
    IDirectionEffectBackend
{
    public bool FailViewPreparation { get; init; }
    public bool FailViewCommit { get; init; }
    public bool ThrowOnDispose { get; init; }
    public List<string> PrepareCalls { get; } = new();
    public List<DirectionCommitToken> CommitTokens { get; } = new();
    public int FinishCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int RollbackCount { get; private set; }

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken) =>
        Prepare($"stage:{stageId}", fail: false, cancellationToken);

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ViewId viewId,
        ViewState? before,
        ViewState? target,
        CancellationToken cancellationToken) =>
        Prepare($"view:{viewId}", FailViewPreparation, cancellationToken);

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ScreenState before,
        ScreenState target,
        CancellationToken cancellationToken) =>
        Prepare("screen", fail: false, cancellationToken);

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StableEffectState before,
        StableEffectState target,
        CancellationToken cancellationToken) =>
        Prepare("effects", fail: false, cancellationToken);

    private Task<IPreparedDirectionComponent> Prepare(
        string name,
        bool fail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareCalls.Add(name);
        if (fail) return Task.FromException<IPreparedDirectionComponent>(new ExpectedDirectionException());
        return Task.FromResult<IPreparedDirectionComponent>(new Component(this, name));
    }

    private sealed class Component : IPreparedDirectionComponent
    {
        private readonly ComponentBackendHarness owner;
        private readonly string name;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Component(ComponentBackendHarness owner, string name)
        {
            this.owner = owner;
            this.name = name;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (owner.FailViewCommit && name == "view:main")
                throw new ExpectedDirectionException();
            owner.CommitTokens.Add(token);
        }

        public void Rollback(DirectionCommitToken token) => owner.RollbackCount++;

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.FinishCount++;
            settlement.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.DisposeCount++;
            if (owner.ThrowOnDispose)
                throw new ExpectedDirectionException();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class VersionStoreResourceManager : IResourceManager
{
    public int LoadedResourceCount => 0;

    public TResource Load<TResource>(ResourceKey key, string? overridePath = null)
        where TResource : class => throw new NotSupportedException();

    public Task<TResource> LoadAsync<TResource>(
        ResourceKey key,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class => throw new NotSupportedException();

    public bool Exists<TResource>(ResourceKey key, string? overridePath = null)
        where TResource : class => false;

    public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
        where TResource : class => new VersionStoreResourceLoader<TResource>(pathPrefix);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class VersionStoreResourceLoader<TResource> : IResourceLoader<TResource>
    where TResource : class
{
    public VersionStoreResourceLoader(string pathPrefix) => PathPrefix = pathPrefix;
    public string PathPrefix { get; }
    public ResourceKey GetKey(string localPath) => new($"{PathPrefix}/{localPath}");
    public bool Exists(string localPath, string? overridePath = null) => false;
    public TResource Load(string localPath, string? overridePath = null) => throw new NotSupportedException();
    public Task<TResource> LoadAsync(
        string localPath,
        string? overridePath = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TResource>(new NotSupportedException());
}

public sealed class FakeCueActivity : ITransientCueActivity
{
    private readonly TaskCompletionSource settlementTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Settlement => settlementTcs.Task;
    public bool IsSkippable { get; set; } = true;
    public bool WasFinished { get; private set; }
    public bool WasCancelled { get; private set; }
    public bool WasDisposed { get; private set; }
    public Exception? FinishException { get; set; }
    public Exception? CancelException { get; set; }
    public Exception? DisposeException { get; set; }

    public void CompleteSettlement() => settlementTcs.TrySetResult();
    public void FailSettlement(Exception ex) => settlementTcs.TrySetException(ex);

    public Task FinishAsync(CancellationToken cancellationToken = default)
    {
        WasFinished = true;
        if (FinishException is not null)
        {
            settlementTcs.TrySetException(FinishException);
            return Task.FromException(FinishException);
        }
        settlementTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        WasCancelled = true;
        if (CancelException is not null)
        {
            settlementTcs.TrySetException(CancelException);
            return Task.FromException(CancelException);
        }
        settlementTcs.TrySetCanceled(cancellationToken);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        if (DisposeException is not null)
            return ValueTask.FromException(DisposeException);
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingCueExecutor : ITransientDirectionCueExecutor
{
    public List<ITransientDirectionCue> Cues { get; } = new();
    public DirectionCommitToken? Token { get; private set; }
    public FakeCueActivity? LastActivity { get; private set; }
    public Func<IEnumerable<ITransientDirectionCue>, ITransientCueActivity?>? ActivityFactory { get; set; }
    public Exception? StartException { get; set; }
    public Exception? ValidateException { get; set; }

    public void Validate(IEnumerable<ITransientDirectionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (ValidateException is not null)
            throw ValidateException;
    }

    public Task<ITransientCueActivity?> StartCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        Token = token;
        var captured = cues.ToList();
        Cues.AddRange(captured);

        if (StartException is not null)
            return Task.FromException<ITransientCueActivity?>(StartException);

        ITransientCueActivity? activity;
        if (ActivityFactory is not null)
        {
            activity = ActivityFactory(captured);
        }
        else
        {
            var fake = new FakeCueActivity();
            fake.CompleteSettlement();
            activity = fake;
        }

        if (activity is FakeCueActivity fakeAct)
            LastActivity = fakeAct;

        return Task.FromResult(activity);
    }
}

public sealed class FakeDirectionBeatRuntime : IDirectionBeatRuntime
{
    public IDirectionDirective? LastSubmitted { get; private set; }

    public bool HasOpenBeat => OpenBeatId is not null;
    public string? OpenBeatId { get; set; }
    public DirectionBeatDefaults? OpenBeatDefaults { get; set; }

    public void Begin(string beatId, string nodeId, DirectionBeatDefaults? defaults = null)
    {
        OpenBeatId = beatId;
        OpenBeatDefaults = defaults;
    }

    public Task<CommittedDirectionTransaction?> SubmitAsync(
        IDirectionDirective directive,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default)
    {
        LastSubmitted = directive;
        return Task.FromResult<CommittedDirectionTransaction?>(null);
    }

    public Task<CommittedDirectionTransaction> EndAsync(
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Settle,
        CancellationToken cancellationToken = default)
    {
        OpenBeatId = null;
        OpenBeatDefaults = null;
        return Task.FromResult<CommittedDirectionTransaction>(null!);
    }

    public void AbortOpen()
    {
        OpenBeatId = null;
        OpenBeatDefaults = null;
    }

    public Task JoinAsync(string beatId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task FinishAsync(string beatId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void ValidateNodeBoundary(string nodeId)
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

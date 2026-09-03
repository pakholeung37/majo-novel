#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;
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
    public bool AutoSettle { get; init; }
    public int CommitCount { get; private set; }
    public int FinishCount { get; private set; }
    public DirectionState? VisibleState { get; private set; }
    public DirectionCommitToken? LastToken { get; private set; }

    public Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PrepareException is not null) throw PrepareException;
        return Task.FromResult<IPreparedDirectionTransaction>(new Prepared(this, plan));
    }

    private sealed class Prepared : IPreparedDirectionTransaction
    {
        private readonly FakeDirectionBackend owner;
        private readonly DirectionPlan plan;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool committed;

        public Prepared(FakeDirectionBackend owner, DirectionPlan plan)
        {
            this.owner = owner;
            this.plan = plan;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("Already committed.");
            committed = true;
            owner.CommitCount++;
            owner.LastToken = token;
            owner.VisibleState = plan.TargetState;
            if (owner.SettlementException is not null)
                settlement.TrySetException(owner.SettlementException);
            else if (owner.AutoSettle) settlement.TrySetResult();
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed) throw new InvalidOperationException("Not committed.");
            owner.FinishCount++;
            settlement.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!committed) settlement.TrySetCanceled();
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

public sealed class CatalogResourceResolver : IDirectionResourceKeyResolver
{
    public ResourceKey ResolveBackdrop(string semanticId) => new(semanticId);
}

public sealed class CatalogPositionResolver : IDirectionPositionResolver
{
    public DirectionPoint Resolve(string value) => new(50, 50);
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

public sealed class RecordingCueExecutor : ITransientDirectionCueExecutor
{
    public List<ITransientDirectionCue> Cues { get; } = new();
    public DirectionCommitToken? Token { get; private set; }

    public Task ExecuteAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken)
    {
        Cues.AddRange(cues);
        return Task.CompletedTask;
    }

    public Task ExecuteCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken)
    {
        Token = token;
        return ExecuteAsync(cues, completion, cancellationToken);
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.State;

/// <summary>Target-only dependency data; preparation must not consult old live dependencies.</summary>
public sealed class RestoreContext
{
    public RestoreContext(
        StateScope scope,
        IReadOnlyDictionary<string, StateRecord> records,
        StateOperationKind operation = StateOperationKind.Restore)
    {
        Scope = scope;
        Operation = operation;
        Records = new ReadOnlyDictionary<string, StateRecord>(
            records.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public StateScope Scope { get; }
    public StateOperationKind Operation { get; }
    public IReadOnlyDictionary<string, StateRecord> Records { get; }
    public StateRecord Require(string key) => Records.TryGetValue(key, out var record)
        ? record : throw new StateCompatibilityException($"Restore target is missing dependency '{key}'.");
}

/// <summary>Game contributors must implement candidate preparation explicitly; no live-restore adapter is provided.</summary>
public interface IPreparedStateContributor : IStateContributor
{
    ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
        StateRecord? record, RestoreContext context, CancellationToken cancellationToken);
}

public interface IPreparedStateRestore : IAsyncDisposable
{
    /// <summary>Publish already prepared objects. No asynchronous work or resource loading is allowed.</summary>
    void Commit();
}

public interface IStateRestoreActivation
{
    Task ActivateAsync(CancellationToken cancellationToken);
}

public interface IStateRestorePresentation
{
    Task WaitForPresentationAsync(CancellationToken cancellationToken);
}

/// <summary>Exactly-once ownership for prepared data, including failed Commit attempts.</summary>
public sealed class PreparedStateRestore : IPreparedStateRestore
{
    private readonly Action commit;
    private readonly Func<ValueTask>? release;
    private bool committed;
    private bool disposed;

    public PreparedStateRestore(Action commit, Func<ValueTask>? release = null)
    {
        this.commit = commit ?? throw new ArgumentNullException(nameof(commit));
        this.release = release;
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("Restore candidate has already been committed.");
        committed = true;
        commit();
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        return release?.Invoke() ?? ValueTask.CompletedTask;
    }
}

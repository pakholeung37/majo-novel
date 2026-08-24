#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.State;

namespace Majo.Runtime.Settings;

public abstract class ScopedStatePersistence : IRuntimeService
{
    private readonly SemaphoreSlim transaction = new(1, 1);
    private readonly IStateSerializer serializer;
    private readonly IStateStorage storage;
    private readonly StateMigrationRegistry migrations;
    private readonly TimeSpan debounce;
    private readonly IRuntimeLogger logger;
    private CancellationTokenSource? debounceCancellation;
    private long revision;
    private bool initialized;

    protected ScopedStatePersistence(
        StateScope scope,
        IReadOnlyList<IStateContributor> contributors,
        IStateSerializer serializer,
        IStateStorage storage,
        StateMigrationRegistry migrations,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        string storageKey,
        TimeSpan debounce,
        bool strictRecovery,
        IRuntimeLogger? logger)
    {
        if (scope is not StateScope.Settings and not StateScope.Global)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        if (debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        Scope = scope;
        Contributors = contributors.Where(value =>
                value.Scope == scope && value.Capability.SupportsPersistence)
            .ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in Contributors)
        {
            if (!keys.Add(contributor.Key))
            {
                throw new DuplicateStateContributorException(contributor.Key);
            }
            foreach (var dependency in contributor.Dependencies)
            {
                if (!keys.Contains(dependency))
                {
                    throw new StateRuntimeException(
                        $"Scoped contributor '{contributor.Key}' dependency '{dependency}' " +
                        "must be registered earlier in the same scope.");
                }
            }
        }
        this.serializer = serializer;
        this.storage = storage;
        this.migrations = migrations;
        RuntimeVersion = runtimeVersion;
        GameBuildId = gameBuildId;
        StorageNamespace = storageNamespace;
        StorageKey = storageKey;
        this.debounce = debounce;
        StrictRecovery = strictRecovery;
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    protected StateScope Scope { get; }
    protected IReadOnlyList<IStateContributor> Contributors { get; }
    protected string RuntimeVersion { get; }
    protected string GameBuildId { get; }
    protected string StorageNamespace { get; }
    protected string StorageKey { get; }
    protected bool StrictRecovery { get; }
    public bool IsDirty { get; private set; }
    public string? LastPersistenceError { get; private set; }
    public ScopedStateRecoveryStatus RecoveryStatus { get; private set; } =
        new(ScopedStateRecoveryKind.None);
    public event Action<Exception>? PersistenceFailed;

    public virtual async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        try
        {
            var bytes = await storage.ReadAsync(StorageKey, cancellationToken);
            var envelope = serializer.Deserialize(bytes);
            ValidateEnvelope(envelope);
            var resolved = migrations.ResolveRecords(envelope.Snapshot, Contributors, Scope);
            foreach (var contributor in Contributors)
            {
                if (resolved.Value.TryGetValue(contributor.Key, out var record))
                {
                    await contributor.PreflightAsync(record, cancellationToken);
                }
            }
            await RestoreResolvedAsync(resolved.Value, cancellationToken);
            RecoveryStatus = new(ScopedStateRecoveryKind.Loaded);
            logger.Info("state", $"{Scope.ToString().ToLowerInvariant()}.loaded",
                $"{Scope} state loaded.");
        }
        catch (StateStorageNotFoundException)
        {
            await ResetContributorsAsync(cancellationToken);
            RecoveryStatus = new(ScopedStateRecoveryKind.Missing);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await HandleLoadFailureAsync(exception, cancellationToken);
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        CancelDebounce();
        if (IsDirty)
        {
            await FlushAsync(cancellationToken);
        }
        initialized = false;
    }

    protected void MarkDirty()
    {
        IsDirty = true;
        Interlocked.Increment(ref revision);
        CancelDebounce();
        debounceCancellation = new CancellationTokenSource();
        _ = DebouncedFlushAsync(debounceCancellation.Token);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirty)
        {
            return;
        }
        await transaction.WaitAsync(cancellationToken);
        try
        {
            if (!IsDirty)
            {
                return;
            }
            var capturedRevision = Interlocked.Read(ref revision);
            var records = new List<StateRecord>();
            foreach (var contributor in Contributors)
            {
                var record = await contributor.CaptureAsync(cancellationToken);
                ValidateRecord(contributor, record);
                records.Add(record);
            }
            var snapshot = new StateSnapshot(
                StateSnapshot.CurrentFormatVersion,
                DateTimeOffset.UtcNow,
                records);
            var metadata = Contributors.Select(value => new StateEnvelopeRecordMetadata(
                value.Key,
                value.SchemaVersion,
                value.Capability));
            var envelope = new StateEnvelope(
                StateEnvelope.CurrentFormatVersion,
                RuntimeVersion,
                GameBuildId,
                StorageNamespace,
                DateTimeOffset.UtcNow,
                Scope,
                snapshot,
                metadata);
            await storage.WriteAsync(StorageKey, serializer.Serialize(envelope), cancellationToken);
            if (capturedRevision == Interlocked.Read(ref revision))
            {
                IsDirty = false;
            }
            LastPersistenceError = null;
            logger.Info("state", $"{Scope.ToString().ToLowerInvariant()}.flush_completed",
                $"{Scope} state flush completed.");
        }
        catch (Exception exception)
        {
            IsDirty = true;
            LastPersistenceError = exception.Message;
            logger.Error("state", $"{Scope.ToString().ToLowerInvariant()}.flush_failed",
                $"{Scope} state flush failed: {exception.Message}", exception);
            PersistenceFailed?.Invoke(exception);
            throw;
        }
        finally
        {
            transaction.Release();
        }
    }

    private async Task HandleLoadFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        LastPersistenceError = exception.Message;
        if (StrictRecovery)
        {
            RecoveryStatus = new(ScopedStateRecoveryKind.StrictFailure, exception.Message,
                BackupKey: (storage as IRecoverableStateStorage)?.GetBackupKey(StorageKey));
            logger.Error("state", $"{Scope.ToString().ToLowerInvariant()}.load_failed",
                $"Strict {Scope} state load failed: {exception.Message}", exception);
            throw exception;
        }

        if (storage is not IRecoverableStateStorage recoverable)
        {
            throw new StateStorageException(
                $"{Scope} state is corrupt and storage cannot quarantine it safely.",
                exception);
        }
        var recovery = await recoverable.QuarantineAsync(StorageKey, cancellationToken);
        await ResetContributorsAsync(cancellationToken);
        RecoveryStatus = new(
            ScopedStateRecoveryKind.RecoveredFromCorruption,
            exception.Message,
            recovery.QuarantineKey,
            recovery.BackupKey);
        logger.Warning("state", $"{Scope.ToString().ToLowerInvariant()}.recovered",
            $"Corrupt {Scope} state was quarantined as '{recovery.QuarantineKey}'.");
    }

    private async Task RestoreResolvedAsync(
        IReadOnlyDictionary<string, StateRecord> records,
        CancellationToken cancellationToken)
    {
        await ResetContributorsAsync(cancellationToken);
        foreach (var contributor in Contributors)
        {
            if (records.TryGetValue(contributor.Key, out var record))
            {
                await contributor.RestoreAsync(record, cancellationToken);
            }
        }
    }

    private async Task ResetContributorsAsync(CancellationToken cancellationToken)
    {
        foreach (var contributor in Contributors.Reverse())
        {
            await contributor.ResetAsync(cancellationToken);
        }
    }

    private void ValidateEnvelope(StateEnvelope envelope)
    {
        if (envelope.Scope != Scope ||
            !string.Equals(envelope.StorageNamespace, StorageNamespace, StringComparison.Ordinal))
        {
            throw new StateCompatibilityException(
                $"Stored {Scope} envelope identity does not match this project.");
        }
    }

    private static void ValidateRecord(IStateContributor contributor, StateRecord record)
    {
        if (record.Key != contributor.Key || record.Scope != contributor.Scope ||
            record.SchemaVersion != contributor.SchemaVersion)
        {
            throw new StateCompatibilityException(
                $"State record '{record.Key}' does not match contributor '{contributor.Key}'.");
        }
    }

    private async Task DebouncedFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(debounce, cancellationToken);
            await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // FlushAsync publishes the structured failure and preserves dirty state.
        }
    }

    private void CancelDebounce()
    {
        debounceCancellation?.Cancel();
        debounceCancellation?.Dispose();
        debounceCancellation = null;
    }
}

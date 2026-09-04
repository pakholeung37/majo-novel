#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public sealed class RuntimeActivityRegistry : IRuntimeActivityRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, ActiveRecord> activities = new(StringComparer.Ordinal);
    private TaskCompletionSource quiescenceTcs = CreateCompletedTcs();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            activities.Clear();
            if (!quiescenceTcs.Task.IsCompleted)
            {
                quiescenceTcs.TrySetResult();
            }
            quiescenceTcs = CreateCompletedTcs();
        }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            activities.Clear();
            if (!quiescenceTcs.Task.IsCompleted)
            {
                quiescenceTcs.TrySetResult();
            }
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<RuntimeActivityInfo> Snapshot()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            return activities.Values
                .Select(r => new RuntimeActivityInfo(
                    r.DiagnosticId,
                    r.Owner,
                    r.Type,
                    r.IsCancelable,
                    r.IsFinishable,
                    now - r.StartedAt,
                    r.Source,
                    r.IsIgnorable))
                .ToArray();
        }
    }

    public bool HasActiveActivities(RuntimeActivityOwner owner, bool includeIgnorable = false)
    {
        lock (gate)
        {
            return activities.Values.Any(r => r.Owner == owner && (includeIgnorable || !r.IsIgnorable));
        }
    }

    public bool HasActiveActivities(bool includeIgnorable = false)
    {
        lock (gate)
        {
            return activities.Values.Any(r => includeIgnorable || !r.IsIgnorable);
        }
    }

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            if (activities.Count == 0 || activities.Values.All(r => r.IsIgnorable))
            {
                return;
            }
            task = quiescenceTcs.Task;
        }
        await task.WaitAsync(cancellationToken);
    }

    public IDisposable BeginActivity(
        RuntimeActivityOwner owner,
        string type,
        string? source = null,
        bool isCancelable = true,
        bool isFinishable = true,
        bool isIgnorable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var diagnosticId = Guid.NewGuid().ToString("N");
        var record = new ActiveRecord(
            diagnosticId,
            owner,
            type,
            source,
            isCancelable,
            isFinishable,
            isIgnorable,
            DateTimeOffset.UtcNow);

        lock (gate)
        {
            var hadNonIgnorable = activities.Values.Any(r => !r.IsIgnorable);
            activities[diagnosticId] = record;

            if (!isIgnorable && !hadNonIgnorable)
            {
                quiescenceTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new ActivityLease(this, diagnosticId);
    }

    private void EndActivity(string diagnosticId)
    {
        lock (gate)
        {
            if (activities.Remove(diagnosticId))
            {
                if (activities.Count == 0 || activities.Values.All(r => r.IsIgnorable))
                {
                    quiescenceTcs.TrySetResult();
                }
            }
        }
    }

    private static TaskCompletionSource CreateCompletedTcs()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private sealed record ActiveRecord(
        string DiagnosticId,
        RuntimeActivityOwner Owner,
        string Type,
        string? Source,
        bool IsCancelable,
        bool IsFinishable,
        bool IsIgnorable,
        DateTimeOffset StartedAt);

    private sealed class ActivityLease : IDisposable
    {
        private RuntimeActivityRegistry? registry;
        private readonly string diagnosticId;

        public ActivityLease(RuntimeActivityRegistry registry, string diagnosticId)
        {
            this.registry = registry;
            this.diagnosticId = diagnosticId;
        }

        public void Dispose()
        {
            var target = Interlocked.Exchange(ref registry, null);
            target?.EndActivity(diagnosticId);
        }
    }
}

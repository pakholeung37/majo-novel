#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Execution;

/// <summary>Shared Camera/Actor clock, accessibility, contribution ownership and termination protocol.</summary>
public sealed class ModifierOperationRunner<TTarget, TDelta> where TTarget : notnull
{
    private readonly IOperationRunner operations;
    private readonly IDirectionAccessibility accessibility;
    private readonly IFrameClock? clock;
    private readonly TDelta identity;
    private readonly Func<TDelta, TDelta, TDelta> combine;
    private readonly Action<TTarget, TDelta> publish;
    private readonly Dictionary<TTarget, Dictionary<Guid, Contribution>> targets = new();
    private readonly object gate = new();

    public ModifierOperationRunner(IOperationRunner operations, IDirectionAccessibility accessibility,
        IFrameClock? clock, TDelta identity, Func<TDelta, TDelta, TDelta> combine, Action<TTarget, TDelta> publish)
    {
        this.operations = operations; this.accessibility = accessibility; this.clock = clock;
        this.identity = identity; this.combine = combine; this.publish = publish;
    }

    public IOperationHandle Start(TTarget target, double duration,
        Func<Action<TDelta>, IFrameClock, CancellationToken, Task> execute)
    {
        var id = Guid.NewGuid();
        Dictionary<Guid, Contribution> group;
        var contribution = new Contribution(identity);
        lock (gate)
        {
            if (!targets.TryGetValue(target, out group!)) targets[target] = group = new();
            group.Add(id, contribution);
        }
        void Remove()
        {
            lock (gate)
            {
                if (!targets.TryGetValue(target, out var current) || !ReferenceEquals(current, group) || !group.Remove(id)) return;
                if (group.Count == 0) targets.Remove(target);
                Publish(target, group);
            }
        }
        void Update(TDelta delta)
        {
            lock (gate)
            {
                if (!targets.TryGetValue(target, out var current) || !ReferenceEquals(current, group) || !group.ContainsKey(id)) return;
                contribution.Delta = delta;
                Publish(target, group);
            }
        }
        try
        {
            var handle = operations.Start(new OperationDefinition(async context =>
            {
                try
                {
                    if (accessibility.ReduceMotion)
                        await (clock ?? context.FrameClock).DelayAsync(TimeSpan.FromSeconds(duration), context.CancellationToken);
                    else await execute(Update, clock ?? context.FrameClock, context.CancellationToken);
                }
                finally { Remove(); }
            }, onFinish: _ => { Remove(); return Task.CompletedTask; },
                onCancel: _ => { Remove(); return Task.CompletedTask; }));
            contribution.Handle = handle;
            return handle;
        }
        catch { Remove(); throw; }
    }

    public async Task ClearTargetAsync(TTarget target, CancellationToken cancellationToken = default)
    {
        IOperationHandle[] handles;
        List<Exception>? errors = null;
        lock (gate)
        {
            if (!targets.Remove(target, out var group)) return;
            handles = group.Values.Select(value => value.Handle).OfType<IOperationHandle>().ToArray();
            try { publish(target, identity); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        foreach (var handle in handles)
            try { handle.Finish(); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        foreach (var handle in handles)
            try { await handle.Completion.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors is not null) throw new AggregateException("Modifier target cleanup failed.", errors);
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        TTarget[] snapshot;
        lock (gate) snapshot = targets.Keys.ToArray();
        List<Exception>? errors = null;
        foreach (var target in snapshot)
            try { await ClearTargetAsync(target, cancellationToken); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors is not null) throw new AggregateException("Modifier cleanup failed.", errors);
    }

    private void Publish(TTarget target, Dictionary<Guid, Contribution> group) =>
        publish(target, group.Values.Aggregate(identity, (delta, value) => combine(delta, value.Delta)));

    private sealed class Contribution(TDelta delta)
    {
        public TDelta Delta = delta;
        public IOperationHandle? Handle;
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotPreparedDirectionComponentGroup : IPreparedDirectionComponent
{
    private readonly IReadOnlyList<IPreparedDirectionComponent> components;
    private readonly object sync = new();
    private bool committed;
    private bool rolledBack;
    private bool disposed;

    public GodotPreparedDirectionComponentGroup(
        params IPreparedDirectionComponent[] components)
    {
        this.components = components ?? throw new ArgumentNullException(nameof(components));
    }

    public Task Settlement => Task.WhenAll(
        components.Select(component => component.Settlement));

    public void Commit(DirectionCommitToken token)
    {
        lock (sync)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Component group is no longer preparable.");
            committed = true;
        }

        try
        {
            foreach (var component in components) component.Commit(token);
        }
        catch (Exception commitException)
        {
            try
            {
                Rollback(token);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Direction component commit failed and compensation also failed.",
                    commitException,
                    rollbackException);
            }
            throw;
        }
    }

    public void Rollback(DirectionCommitToken token)
    {
        lock (sync)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
        }

        List<Exception>? failures = null;
        foreach (var component in components.Reverse())
        {
            try { component.Rollback(token); }
            catch (Exception exception) { (failures ??= new()).Add(exception); }
        }
        if (failures is not null)
            throw new AggregateException("One or more Direction components failed to roll back.", failures);
    }

    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (!committed || rolledBack || disposed)
            throw new InvalidOperationException("Component group cannot finish in its current state.");
        foreach (var component in components)
            await component.FinishAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        List<Exception>? failures = null;
        foreach (var component in components.Reverse())
        {
            try { await component.DisposeAsync(); }
            catch (Exception exception) { (failures ??= new()).Add(exception); }
        }
        if (failures is not null)
            throw new AggregateException("One or more Direction components failed to dispose.", failures);
    }
}

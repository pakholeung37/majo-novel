#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotPreparedDirectionComponentGroup : IPreparedDirectionComponent
{
    private readonly IReadOnlyList<IPreparedDirectionComponent> components;
    private bool committed;
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
        if (committed) throw new InvalidOperationException("Component group already committed.");
        committed = true;
        foreach (var component in components) component.Commit(token);
    }

    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (!committed) throw new InvalidOperationException("Component group has not committed.");
        foreach (var component in components)
            await component.FinishAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (var component in components.Reverse())
            await component.DisposeAsync();
    }
}

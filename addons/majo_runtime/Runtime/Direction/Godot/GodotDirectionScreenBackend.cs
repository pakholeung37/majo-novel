#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionScreenBackend : IDirectionScreenBackend
{
    private readonly GodotDirectionRenderHost host;

    public GodotDirectionScreenBackend(GodotDirectionRenderHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ScreenState before,
        ScreenState target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        DirectionLayoutCatalog.RequiredSlots(target.Layout);
        foreach (var viewId in target.Bindings.Values)
            host.GetOrCreateView(viewId);

        return Task.FromResult<IPreparedDirectionComponent>(
            new ImmediatePreparedDirectionComponent(_ => host.PublishScreen(target)));
    }
}

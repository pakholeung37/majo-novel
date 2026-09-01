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
        if (target.Layout != LayoutId.Single ||
            target.Bindings.Count != 1 ||
            !target.Bindings.TryGetValue(LayoutSlotId.Main, out var viewId) ||
            viewId != ViewId.Main)
        {
            throw new DirectionReferenceException(
                "The first Godot Direction Screen backend only supports layout 'single main:main'.");
        }

        return Task.FromResult<IPreparedDirectionComponent>(
            new ImmediatePreparedDirectionComponent(_ => host.PublishScreen(target)));
    }
}

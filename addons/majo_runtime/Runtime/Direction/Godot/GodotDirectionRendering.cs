#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

internal static class GodotDirectionRendering
{
    public static DirectionTransitionSpec ResolveTransition(
        DirectionPlan plan,
        Func<string, bool> matches)
    {
        var specs = plan.Transitions
            .Where(transition => transition.StablePaths.Any(path => path == "*" || matches(path)))
            .Select(transition => transition.Spec)
            .Distinct()
            .ToArray();
        return specs.Length switch
        {
            0 => DirectionTransitionSpec.Cut,
            1 => specs[0],
            _ => throw new DirectionException(
                "The first Godot Direction backend cannot animate multiple transition " +
                "specifications for the same render component."),
        };
    }

    public static void Configure(Tween tween, EasingId easing, IEasingRegistry? registry = null)
    {
        GodotEasingAdapter.Configure(tween, easing, registry);
    }

    public static Color ToGodot(DirectionColor color, float? alpha = null) =>
        new(color.R, color.G, color.B, alpha ?? color.A);

    public static ViewPose Interpolate(ViewPose from, ViewPose to, float progress) => new(
        new DirectionPoint(
            Mathf.Lerp(from.Center.X, to.Center.X, progress),
            Mathf.Lerp(from.Center.Y, to.Center.Y, progress)),
        Mathf.Lerp(from.Zoom, to.Zoom, progress),
        Mathf.LerpAngle(
            Mathf.DegToRad(from.RotationDegrees),
            Mathf.DegToRad(to.RotationDegrees),
            progress) * 180 / Mathf.Pi);

    public static void Kill(Tween? tween)
    {
        if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Kill();
    }
}

internal sealed class ImmediatePreparedDirectionComponent :
    Rendering.IPreparedDirectionComponent
{
    private readonly Action<DirectionCommitToken>? commit;
    private readonly TaskCompletionSource settlement = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool committed;

    public ImmediatePreparedDirectionComponent(Action<DirectionCommitToken>? commit = null)
    {
        this.commit = commit;
    }

    public Task Settlement => settlement.Task;

    public void Commit(DirectionCommitToken token)
    {
        if (committed) throw new InvalidOperationException("Component already committed.");
        committed = true;
        commit?.Invoke(token);
        settlement.TrySetResult();
    }

    public ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!committed) throw new InvalidOperationException("Component has not committed.");
        settlement.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!committed) settlement.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}

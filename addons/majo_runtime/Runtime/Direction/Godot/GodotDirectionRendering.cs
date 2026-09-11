#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Transitions;
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

    public static void ValidateTransition(DirectionTransitionSpec transition, TransitionDomains domain,
        ITransitionDriverRegistry drivers, IEasingRegistry easings)
    {
        transition.Validate();
        easings.GetRequired(transition.Easing);
        if (transition.Kind == DirectionTransitionId.Move)
        {
            if (domain == TransitionDomains.Stage) throw new DirectionException("Backdrop cannot use a move transition.");
            return;
        }
        if (!drivers.Supports(transition.Kind, domain))
            throw new DirectionException($"Transition '{transition.Kind}' does not support {domain}.");
        var driver = drivers.GetRequired(transition.Kind);
        if (!driver.SupportsCustomEasing && !BuiltInEasings.All.Any(easing => easing.Id == transition.Easing))
            throw new DirectionException($"Transition '{transition.Kind}' does not support custom easing '{transition.Easing}'.");
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
            progress) * 180 / Mathf.Pi)
    {
        Dolly = Mathf.Lerp(from.Dolly, to.Dolly, progress),
        BackgroundOffset = new DirectionPoint(
            Mathf.Lerp(from.BackgroundOffset.X, to.BackgroundOffset.X, progress),
            Mathf.Lerp(from.BackgroundOffset.Y, to.BackgroundOffset.Y, progress)),
        BackgroundScale = Mathf.Lerp(from.BackgroundScale, to.BackgroundScale, progress),
    };

    public static ViewParallaxState Interpolate(ViewParallaxState from, ViewParallaxState to, float progress) => new(
        Mathf.Lerp(from.Strength, to.Strength, progress),
        new DirectionPoint(Mathf.Lerp(from.Anchor.X, to.Anchor.X, progress), Mathf.Lerp(from.Anchor.Y, to.Anchor.Y, progress)),
        new DirectionPoint(Mathf.Lerp(from.LimitPercent.X, to.LimitPercent.X, progress), Mathf.Lerp(from.LimitPercent.Y, to.LimitPercent.Y, progress)))
    { UseDepthProjection = to.UseDepthProjection };

    public static ViewIdleState Interpolate(ViewIdleState from, ViewIdleState to, float progress) => new(
        new DirectionPoint(
            Mathf.Lerp(from.AmplitudePercent.X, to.AmplitudePercent.X, progress),
            Mathf.Lerp(from.AmplitudePercent.Y, to.AmplitudePercent.Y, progress)),
        Mathf.Lerp((float)from.PeriodSeconds, (float)to.PeriodSeconds, progress),
        from.PhaseRadians);

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

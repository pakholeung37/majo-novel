#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Operations.Godot;

public static class GodotTweenOperation
{
    public static OperationDefinition CreateFloatMethodTween(
        Node owner,
        Func<float> getCurrentValue,
        Action<float> applyValue,
        float finalValue,
        double durationSeconds,
        string? id = null,
        string? groupId = null,
        Tween.TransitionType transition = Tween.TransitionType.Linear,
        Tween.EaseType ease = Tween.EaseType.InOut,
        bool ignoreTimeScale = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(getCurrentValue);
        ArgumentNullException.ThrowIfNull(applyValue);
        if (!float.IsFinite(finalValue))
        {
            throw new ArgumentOutOfRangeException(nameof(finalValue));
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds), "Duration must be finite and non-negative.");
        }

        Tween? tween = null;
        return new OperationDefinition(
            async context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                EnsureValid(owner, nameof(owner));
                var initialValue = getCurrentValue();
                tween = owner.CreateTween();
                tween.SetTrans(transition);
                tween.SetEase(ease);
                tween.SetIgnoreTimeScale(ignoreTimeScale);
                tween.TweenMethod(
                    Callable.From<float>(applyValue),
                    initialValue,
                    finalValue,
                    durationSeconds);
                await WaitForTweenAsync(tween, context.CancellationToken);
                tween = null;
                applyValue(finalValue);
            },
            id,
            groupId,
            _ =>
            {
                KillTween(tween);
                tween = null;
                EnsureValid(owner, nameof(owner));
                applyValue(finalValue);
                return Task.CompletedTask;
            },
            _ =>
            {
                KillTween(tween);
                tween = null;
                return Task.CompletedTask;
            });
    }

    public static OperationDefinition CreatePropertyTween(
        Node owner,
        GodotObject target,
        NodePath property,
        Variant finalValue,
        double durationSeconds,
        string? id = null,
        string? groupId = null,
        Tween.TransitionType transition = Tween.TransitionType.Linear,
        Tween.EaseType ease = Tween.EaseType.InOut,
        bool ignoreTimeScale = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds), "Duration must be finite and non-negative.");
        }

        Tween? tween = null;

        return new OperationDefinition(
            async context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                EnsureValid(owner, nameof(owner));
                EnsureValid(target, nameof(target));

                tween = owner.CreateTween();
                tween.SetTrans(transition);
                tween.SetEase(ease);
                tween.SetIgnoreTimeScale(ignoreTimeScale);
                tween.TweenProperty(target, property, finalValue, durationSeconds);
                await WaitForTweenAsync(tween, context.CancellationToken);
                tween = null;
            },
            id,
            groupId,
            _ =>
            {
                KillTween(tween);
                tween = null;
                EnsureValid(target, nameof(target));
                target.SetIndexed(property, finalValue);
                return Task.CompletedTask;
            },
            _ =>
            {
                KillTween(tween);
                tween = null;
                return Task.CompletedTask;
            });
    }

    private static async Task WaitForTweenAsync(
        Tween tween,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished() => completion.TrySetResult();

        tween.Finished += OnFinished;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        try
        {
            await completion.Task;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(tween))
            {
                tween.Finished -= OnFinished;
            }
        }
    }

    private static void KillTween(Tween? tween)
    {
        if (tween is not null && GodotObject.IsInstanceValid(tween))
        {
            tween.Kill();
        }
    }

    private static void EnsureValid(GodotObject value, string parameterName)
    {
        if (!GodotObject.IsInstanceValid(value))
        {
            throw new InvalidOperationException(
                $"Godot object '{parameterName}' is no longer valid.");
        }
    }
}

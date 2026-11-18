#nullable enable
using System;
using Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Operations.Godot;

/// <summary>A linear clock with one easing evaluation and an explicit failure channel.</summary>
public sealed class GodotProgressTween : IDisposable
{
    private readonly Node owner;
    private readonly Action<Exception> fail;
    private Tween? tween;
    private bool disposed;

    public GodotProgressTween(Node owner, AnimationTiming timing, EasingDefinition easing,
        Action<float> apply, Action complete, Action<Exception> fail)
    {
        this.owner = owner;
        this.fail = fail;
        try
        {
            apply(Sample(easing, 0));
            tween = owner.CreateTween();
            tween.SetTrans(Tween.TransitionType.Linear);
            tween.SetEase(Tween.EaseType.InOut);
            tween.SetIgnoreTimeScale(timing.TimeMode == AnimationTimeMode.Unscaled);
            tween.TweenMethod(Callable.From<float>(p =>
            {
                if (disposed) return;
                try { apply(Sample(easing, p)); }
                catch (Exception exception) { Fail(exception); }
            }), 0f, 1f, timing.DurationSeconds);
            tween.Finished += () =>
            {
                if (disposed) return;
                try { apply(1); Dispose(); complete(); }
                catch (Exception exception) { Fail(exception); }
            };
            owner.TreeExiting += OnOwnerExiting;
        }
        catch (Exception exception) { Fail(exception); }
    }

    public static float Sample(EasingDefinition easing, float progress)
    {
        var value = (float)easing.Evaluate(Math.Clamp(progress, 0, 1));
        if (!float.IsFinite(value))
            throw new InvalidOperationException($"Easing '{easing.Id}' returned a non-finite value at {progress}.");
        return value;
    }

    private void OnOwnerExiting() => Fail(new InvalidOperationException("Animation owner left the scene tree."));

    private void Fail(Exception exception)
    {
        Dispose();
        fail(exception);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (GodotObject.IsInstanceValid(owner)) owner.TreeExiting -= OnOwnerExiting;
        if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Kill();
        tween = null;
    }
}

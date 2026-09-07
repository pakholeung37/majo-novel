#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Timing;

public sealed record EasingDefinition(
    EasingId Id,
    Func<double, double> Evaluate,
    IReadOnlyList<string>? Aliases = null)
{
    public EasingDefinition Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("Easing ID cannot be empty.", nameof(Id));
        }

        ArgumentNullException.ThrowIfNull(Evaluate);

        var v0 = Evaluate(0.0);
        if (!double.IsFinite(v0))
        {
            throw new ArgumentException($"Easing '{Id}' must return a finite value at t=0 (got {v0}).", nameof(Evaluate));
        }

        var v1 = Evaluate(1.0);
        if (!double.IsFinite(v1))
        {
            throw new ArgumentException($"Easing '{Id}' must return a finite value at t=1 (got {v1}).", nameof(Evaluate));
        }

        var vHalf = Evaluate(0.5);
        if (!double.IsFinite(vHalf))
        {
            throw new ArgumentException($"Easing '{Id}' must return a finite value at t=0.5 (got {vHalf}).", nameof(Evaluate));
        }

        return this;
    }
}

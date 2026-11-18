#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Camera;

public interface ICameraModifier
{
    string EffectName { get; }

    void Validate(IReadOnlyDictionary<string, string> parameters, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
            throw new DirectionException(
                "Camera modifier duration must be finite and non-negative.");
    }

    Task ExecuteAsync(
        ViewId viewId,
        IReadOnlyDictionary<string, string> parameters,
        double durationSeconds,
        Action<CameraPoseDelta> applyDelta,
        IFrameClock frameClock,
        CancellationToken cancellationToken);
}

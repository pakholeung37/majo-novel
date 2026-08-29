#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Travel;

public interface IDialogueTravelManager : IRuntimeService
{
    event Action<DialogueTravelTimelineChanged>? TimelineChanged;

    event Action<DialogueTravelCompleted>? TravelCompleted;

    event Action<DialogueTravelFailed>? TravelFailed;

    IReadOnlyList<DialogueTravelEntry> Entries { get; }

    int CurrentIndex { get; }

    int Capacity { get; }

    bool IsTravelling { get; }

    bool CanStepBack { get; }

    bool CanStepForward { get; }

    Task<bool> StepBackAsync(CancellationToken cancellationToken = default);

    Task<bool> StepForwardAsync(CancellationToken cancellationToken = default);

    Task<bool> TravelToAsync(
        long sequence,
        CancellationToken cancellationToken = default);

    void Clear();
}

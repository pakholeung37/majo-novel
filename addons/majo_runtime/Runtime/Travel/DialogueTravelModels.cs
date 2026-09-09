#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Script;
using Majo.Runtime.State;

namespace Majo.Runtime.Travel;

public sealed record DialogueTravelEntry(
    long Sequence,
    ScriptPlaybackSpot Spot,
    StateSnapshot Snapshot,
    string? Speaker,
    string Text,
    DateTimeOffset CapturedAt,
    long OccurrenceSequence = 0,
    long SessionGeneration = 0);

public sealed record DialogueTravelTimelineChanged(
    IReadOnlyList<DialogueTravelEntry> Entries,
    int CurrentIndex);

public sealed record DialogueTravelCompleted(
    DialogueTravelEntry Entry,
    int PreviousIndex,
    int CurrentIndex);

public sealed record DialogueTravelFailed(
    DialogueTravelEntry? Entry,
    Exception Exception);

#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.History;

public sealed record HistoryEntry(
    long Sequence,
    string LineId,
    string? Speaker,
    string Text,
    string? VoiceId);

public sealed record HistoryOpenStateChanged(bool IsOpen);

public sealed record HistoryEntriesChanged(
    IReadOnlyList<HistoryEntry> Entries);

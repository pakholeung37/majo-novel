#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.History;

public sealed record HistoryEntry(
    long Sequence,
    string LineId,
    string? Speaker,
    string Text,
    string? VoiceId,
    string? Locale = null,
    string? VoicePath = null,
    string? VoiceLocale = null);

public sealed record HistoryOpenStateChanged(bool IsOpen);

public sealed record HistoryEntriesChanged(
    IReadOnlyList<HistoryEntry> Entries);

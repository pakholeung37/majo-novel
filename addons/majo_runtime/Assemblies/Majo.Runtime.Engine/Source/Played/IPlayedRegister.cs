#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Played;

public enum PlayedLocalePolicy
{
    Shared,
    PerLocale,
}

public enum PlayedContentChangePolicy
{
    KeepPlayed,
    TreatAsUnread,
}

public sealed record PlayedIdentity(
    string ProjectId,
    string LineId,
    string? Locale = null);

public sealed record PlayedPresentation(
    PlayedIdentity Identity,
    string? ContentFingerprint,
    bool WasPlayedBeforePresentation);

public sealed record PlayedEntry(
    PlayedIdentity Identity,
    string? ContentFingerprint);

public interface IPlayedRegister : IRuntimeService
{
    event Action? Changed;

    PlayedLocalePolicy LocalePolicy { get; }

    PlayedContentChangePolicy ContentChangePolicy { get; }

    IReadOnlyCollection<PlayedEntry> Entries { get; }

    PlayedPresentation? CurrentPresentation { get; }

    bool IsPlayed(
        string projectId,
        string lineId,
        string? locale = null,
        string? contentFingerprint = null);

    void Clear();
}

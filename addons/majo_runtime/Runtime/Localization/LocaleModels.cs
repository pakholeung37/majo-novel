#nullable enable

using System.Collections.Generic;

namespace Majo.Runtime.Localization;

public enum LocaleFallbackSource
{
    Exact,
    Parent,
    Default,
    Missing,
}

public sealed record LocaleResolutionResult<T>(
    string RequestedLocale,
    string? ResolvedLocale,
    LocaleFallbackSource FallbackSource,
    T? Value,
    string Key)
{
    public bool IsResolved => FallbackSource != LocaleFallbackSource.Missing && Value is not null;
}

public static class LocaleResolutionResult
{
    public static LocaleResolutionResult<T> Exact<T>(string locale, string key, T value) =>
        new(locale, locale, LocaleFallbackSource.Exact, value, key);

    public static LocaleResolutionResult<T> Parent<T>(string requested, string resolved, string key, T value) =>
        new(requested, resolved, LocaleFallbackSource.Parent, value, key);

    public static LocaleResolutionResult<T> Default<T>(string requested, string resolved, string key, T value) =>
        new(requested, resolved, LocaleFallbackSource.Default, value, key);

    public static LocaleResolutionResult<T> Missing<T>(string requested, string key) =>
        new(requested, null, LocaleFallbackSource.Missing, default, key);
}

public enum LocaleSwitchState
{
    Idle,
    Preflighting,
    PendingPresentationBoundary,
    Applying,
    Faulted,
}

public sealed record LocaleSwitchResult(
    bool Success,
    string PreviousLocale,
    string TargetLocale,
    string AppliedLocale,
    string? FailureReason = null)
{
    public static LocaleSwitchResult Succeeded(string previous, string applied) =>
        new(true, previous, applied, applied, null);

    public static LocaleSwitchResult Failed(string previous, string target, string reason) =>
        new(false, previous, target, previous, reason);
}

public sealed record LocaleSwitchStartingEvent(
    string PreviousLocale,
    string TargetLocale,
    bool IsQueued);

public sealed record LocaleChangedEvent(
    string PreviousLocale,
    string CurrentLocale);

public sealed record LocaleSwitchFailedEvent(
    string PreviousLocale,
    string TargetLocale,
    string Reason);

public sealed record LocaleDiagnosticsSnapshot(
    string DialogueLocale,
    string VoiceLocale,
    string DefaultLocale,
    string DefaultVoiceLocale,
    IReadOnlyList<string> AvailableLocales,
    IReadOnlyList<string> DialogueFallbackChain,
    IReadOnlyList<string> VoiceFallbackChain,
    LocaleSwitchState SwitchState,
    int MissingKeyCount);

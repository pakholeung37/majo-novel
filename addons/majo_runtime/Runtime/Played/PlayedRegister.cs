#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.State;

namespace Majo.Runtime.Played;

public sealed class PlayedRegister : IPlayedRegister, IStateContributor, IStateDirtyNotifier
{
    public const string StateKey = "majo.global.played";
    private const string FingerprintMetadataPrefix = "majo:fingerprint=";
    private readonly object sync = new();
    private readonly IDialoguePlayer dialogue;
    private readonly IDialoguePresentation presentation;
    private readonly Func<string> localeProvider;
    private readonly IRuntimeLogger logger;
    private readonly Dictionary<string, PlayedEntry> entries = new(StringComparer.Ordinal);
    private bool initialized;

    public PlayedRegister(
        IDialoguePlayer dialogue,
        IDialoguePresentation presentation,
        PlayedLocalePolicy localePolicy = PlayedLocalePolicy.Shared,
        PlayedContentChangePolicy contentChangePolicy = PlayedContentChangePolicy.KeepPlayed,
        Func<string>? localeProvider = null,
        IRuntimeLogger? logger = null)
    {
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!Enum.IsDefined(localePolicy)) throw new ArgumentOutOfRangeException(nameof(localePolicy));
        if (!Enum.IsDefined(contentChangePolicy)) throw new ArgumentOutOfRangeException(nameof(contentChangePolicy));
        LocalePolicy = localePolicy;
        ContentChangePolicy = contentChangePolicy;
        this.localeProvider = localeProvider ?? (() => string.Empty);
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action? Changed;
    public event Action? StateDirty;

    public PlayedLocalePolicy LocalePolicy { get; }
    public PlayedContentChangePolicy ContentChangePolicy { get; }
    public PlayedPresentation? CurrentPresentation { get; private set; }
    public IReadOnlyCollection<PlayedEntry> Entries
    {
        get { lock (sync) { return entries.Values.OrderBy(KeyFor).ToArray(); } }
    }

    public string Key => StateKey;
    public StateScope Scope => StateScope.Global;
    public int SchemaVersion => 1;
    public StateContributorCapability Capability => StateContributorCapability.Durable;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (initialized) return Task.CompletedTask;
        dialogue.LinePresented += HandleLinePresented;
        dialogue.Completed += HandleDialogueEnded;
        dialogue.Stopped += HandleDialogueEnded;
        presentation.LineDisplayCompleted += HandleLineDisplayCompleted;
        initialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentPresentation = null;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!initialized) return Task.CompletedTask;
        dialogue.LinePresented -= HandleLinePresented;
        dialogue.Completed -= HandleDialogueEnded;
        dialogue.Stopped -= HandleDialogueEnded;
        presentation.LineDisplayCompleted -= HandleLineDisplayCompleted;
        CurrentPresentation = null;
        initialized = false;
        return Task.CompletedTask;
    }

    public bool IsPlayed(
        string projectId,
        string lineId,
        string? locale = null,
        string? contentFingerprint = null)
    {
        var identity = NormalizeIdentity(projectId, lineId, locale);
        lock (sync)
        {
            if (!entries.TryGetValue(KeyFor(identity), out var entry)) return false;
            return ContentChangePolicy != PlayedContentChangePolicy.TreatAsUnread ||
                contentFingerprint is null || entry.ContentFingerprint is null ||
                string.Equals(entry.ContentFingerprint, contentFingerprint, StringComparison.Ordinal);
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            if (entries.Count == 0) return;
            entries.Clear();
        }
        Changed?.Invoke();
        StateDirty?.Invoke();
    }

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlayedEntry[] snapshot;
        lock (sync) { snapshot = entries.Values.OrderBy(KeyFor).ToArray(); }
        return ValueTask.FromResult(new StateRecord(
            Key, Scope, SchemaVersion, JsonSerializer.SerializeToElement(new Payload(
                LocalePolicy, ContentChangePolicy, snapshot))));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Decode(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restored = Decode(record);
        lock (sync)
        {
            entries.Clear();
            foreach (var entry in restored) entries.Add(KeyFor(entry.Identity), entry);
        }
        CurrentPresentation = null;
        Changed?.Invoke();
        return ValueTask.CompletedTask;
    }

    ValueTask IStateContributor.ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) { entries.Clear(); }
        CurrentPresentation = null;
        Changed?.Invoke();
        return ValueTask.CompletedTask;
    }

    private void HandleLinePresented(DialogueLine line)
    {
        if (line.Origin == DialoguePresentationOrigin.HotReloadReplay)
        {
            return;
        }
        var projectId = dialogue.CurrentProjectId ?? throw new InvalidOperationException(
            "A presented line must belong to a dialogue project.");
        var fingerprint = MetadataValue(line.Metadata, FingerprintMetadataPrefix);
        var locale = LocalePolicy == PlayedLocalePolicy.PerLocale ? localeProvider() : null;
        var identity = NormalizeIdentity(projectId, line.Id, locale);
        CurrentPresentation = new PlayedPresentation(
            identity,
            fingerprint,
            IsPlayed(projectId, line.Id, locale, fingerprint));
    }

    private void HandleLineDisplayCompleted()
    {
        var current = CurrentPresentation;
        if (current is null) return;
        var entry = new PlayedEntry(current.Identity, current.ContentFingerprint);
        var changed = false;
        lock (sync)
        {
            var key = KeyFor(entry);
            if (!entries.TryGetValue(key, out var existing) || existing != entry)
            {
                entries[key] = entry;
                changed = true;
            }
        }
        if (!changed) return;
        logger.Debug("played", "played.line_marked", $"Line '{entry.Identity.LineId}' was marked played.");
        Changed?.Invoke();
        StateDirty?.Invoke();
    }

    private void HandleDialogueEnded(DialogueCompletion _) => CurrentPresentation = null;

    private PlayedEntry[] Decode(StateRecord record)
    {
        Payload payload;
        try
        {
            payload = record.Payload.Deserialize<Payload>() ?? throw new JsonException();
        }
        catch (Exception exception)
        {
            throw new StateSnapshotException($"State record '{Key}' has an invalid payload.", exception);
        }
        if (payload.LocalePolicy != LocalePolicy || payload.ContentChangePolicy != ContentChangePolicy)
        {
            throw new StateSnapshotException(
                $"Played identity policy mismatch. Stored: {payload.LocalePolicy}/{payload.ContentChangePolicy}; " +
                $"runtime: {LocalePolicy}/{ContentChangePolicy}.");
        }
        var result = new Dictionary<string, PlayedEntry>(StringComparer.Ordinal);
        foreach (var item in payload.Entries ?? Array.Empty<PlayedEntry>())
        {
            var identity = NormalizeIdentity(item.Identity.ProjectId, item.Identity.LineId, item.Identity.Locale);
            var normalized = item with { Identity = identity };
            if (!result.TryAdd(KeyFor(identity), normalized))
                throw new StateSnapshotException($"Played state contains duplicate line '{identity.LineId}'.");
        }
        return result.Values.OrderBy(KeyFor).ToArray();
    }

    private PlayedIdentity NormalizeIdentity(string projectId, string lineId, string? locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        var normalizedLocale = LocalePolicy == PlayedLocalePolicy.PerLocale
            ? string.IsNullOrWhiteSpace(locale) ? throw new ArgumentException("Locale is required by the played identity policy.", nameof(locale)) : locale.Trim()
            : null;
        return new PlayedIdentity(projectId.Trim(), lineId.Trim(), normalizedLocale);
    }

    private static string KeyFor(PlayedEntry entry) => KeyFor(entry.Identity);
    private static string KeyFor(PlayedIdentity identity) =>
        $"{identity.ProjectId}\u001f{identity.LineId}\u001f{identity.Locale ?? string.Empty}";

    private static string? MetadataValue(IReadOnlyList<string> metadata, string prefix) =>
        metadata.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private sealed record Payload(
        PlayedLocalePolicy LocalePolicy,
        PlayedContentChangePolicy ContentChangePolicy,
        PlayedEntry[] Entries);
}

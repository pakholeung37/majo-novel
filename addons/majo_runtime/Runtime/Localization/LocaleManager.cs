#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Localization;

public sealed class LocaleManager : ILocaleManager
{
    private readonly object sync = new();
    private readonly SemaphoreSlim switchGate = new(1, 1);
    private readonly SemaphoreSlim voiceSwitchGate = new(1, 1);
    private CancellationTokenSource switchLifetime = new();
    private readonly List<string> availableLocales = new();
    private readonly Func<bool>? isBusyCheck;
    private readonly Func<string, Task>? onLocaleApplied;
    private readonly Func<string, Task>? onVoiceLocaleApplied;
    private readonly Func<CancellationToken, Task>? waitForSafeBoundary;
    private readonly Func<string, CancellationToken, Task>? preflightLocale;
    private readonly Action<string>? applyEngineLocale;
    private readonly IRuntimeLogger logger;
    private int missingKeyCount;
    private bool isInitialized;

    public LocaleManager(
        string defaultLocale = "en",
        IEnumerable<string>? availableLocales = null,
        IFontCatalog? fonts = null,
        Func<bool>? isBusyCheck = null,
        Func<string, Task>? onLocaleApplied = null,
        Action<string>? applyEngineLocale = null,
        IRuntimeLogger? logger = null,
        Func<CancellationToken, Task>? waitForSafeBoundary = null,
        Func<string, CancellationToken, Task>? preflightLocale = null,
        string? defaultVoiceLocale = null,
        Func<string, Task>? onVoiceLocaleApplied = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLocale);
        DefaultLocale = NormalizeLocale(defaultLocale);
        DefaultVoiceLocale = NormalizeLocale(defaultVoiceLocale ?? defaultLocale);
        DialogueLocale = DefaultLocale;
        VoiceLocale = DefaultVoiceLocale;
        Fonts = fonts ?? new FontCatalog();
        this.isBusyCheck = isBusyCheck;
        this.onLocaleApplied = onLocaleApplied;
        this.applyEngineLocale = applyEngineLocale;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.waitForSafeBoundary = waitForSafeBoundary;
        this.preflightLocale = preflightLocale;
        this.onVoiceLocaleApplied = onVoiceLocaleApplied;

        var locales = (availableLocales ?? [DefaultLocale])
            .Select(NormalizeLocale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!locales.Contains(DefaultLocale, StringComparer.OrdinalIgnoreCase))
        {
            locales.Insert(0, DefaultLocale);
        }

        this.availableLocales.AddRange(locales);
    }

    public event Action<LocaleSwitchStartingEvent>? SwitchStarting;
    public event Action<LocaleChangedEvent>? DialogueLocaleChanged;
    public event Action<LocaleChangedEvent>? VoiceLocaleChanged;
    public event Action<LocaleSwitchFailedEvent>? SwitchFailed;

    public string DialogueLocale { get; private set; }
    public string VoiceLocale { get; private set; }
    public string CurrentLocale => DialogueLocale;
    public string DefaultLocale { get; }
    public string DefaultVoiceLocale { get; }
    public IReadOnlyList<string> AvailableLocales
    {
        get { lock (sync) { return availableLocales.ToArray(); } }
    }
    public LocaleSwitchState SwitchState { get; private set; } = LocaleSwitchState.Idle;
    public bool IsInitialized => isInitialized;
    public IFontCatalog Fonts { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (switchLifetime.IsCancellationRequested)
            {
                switchLifetime.Dispose();
                switchLifetime = new CancellationTokenSource();
            }
        }
        isInitialized = true;
        ApplyGodotEngineLocale(DialogueLocale);
        if (onVoiceLocaleApplied is not null)
        {
            return onVoiceLocaleApplied(VoiceLocale);
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenewSwitchLifetime();
        SwitchState = LocaleSwitchState.Idle;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            switchLifetime.Cancel();
        }
        isInitialized = false;
        SwitchState = LocaleSwitchState.Idle;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetFallbackChain(string locale) =>
        BuildFallbackChain(locale, DefaultLocale);

    public IReadOnlyList<string> GetDialogueFallbackChain(string locale) =>
        BuildFallbackChain(locale, DefaultLocale);

    public IReadOnlyList<string> GetVoiceFallbackChain(string locale) =>
        BuildFallbackChain(locale, DefaultVoiceLocale);

    private static IReadOnlyList<string> BuildFallbackChain(string locale, string defaultLocale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var normalized = NormalizeLocale(locale);
        var chain = new List<string> { normalized };

        // Split on hyphens to create parent chain (e.g., zh-Hans-CN -> zh-Hans -> zh)
        var parts = normalized.Split('-');
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            var parent = string.Join("-", parts.Take(i));
            if (!chain.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(parent);
            }
        }

        if (!chain.Contains(defaultLocale, StringComparer.OrdinalIgnoreCase))
        {
            chain.Add(defaultLocale);
        }

        return chain;
    }

    public void RegisterAvailableLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var normalized = NormalizeLocale(locale);
        lock (sync)
        {
            if (!availableLocales.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                availableLocales.Add(normalized);
            }
        }
    }

    public void RequestLocaleChange(string targetLocale) =>
        _ = ChangeDialogueLocaleAsync(targetLocale);

    public Task<LocaleSwitchResult> ChangeLocaleAsync(
        string targetLocale,
        CancellationToken cancellationToken = default) =>
        ChangeDialogueLocaleAsync(targetLocale, cancellationToken);

    public async Task<LocaleSwitchResult> ChangeDialogueLocaleAsync(
        string targetLocale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);
        var normalized = NormalizeLocale(targetLocale);
        CancellationToken lifetimeToken;
        lock (sync)
        {
            lifetimeToken = switchLifetime.Token;
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        var transactionToken = linkedCancellation.Token;

        await switchGate.WaitAsync(transactionToken);
        try
        {
            return await ChangeLocaleCoreAsync(normalized, transactionToken);
        }
        catch (OperationCanceledException)
        {
            SwitchState = LocaleSwitchState.Idle;
            throw;
        }
        finally
        {
            switchGate.Release();
        }
    }

    private async Task<LocaleSwitchResult> ChangeLocaleCoreAsync(
        string normalized,
        CancellationToken cancellationToken)
    {

        if (string.Equals(DialogueLocale, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return LocaleSwitchResult.Succeeded(DialogueLocale, DialogueLocale);
        }

        var previousLocale = DialogueLocale;
        logger.Info(
            "locale",
            "locale.switch_requested",
            $"Locale switch requested from '{previousLocale}' to '{normalized}'.",
            properties: new Dictionary<string, object?>
            {
                ["previousLocale"] = previousLocale,
                ["targetLocale"] = normalized,
            });

        SwitchState = LocaleSwitchState.Preflighting;

        // Preflight verification
        var preflightFailure = PreflightTargetLocale(normalized);
        if (preflightFailure is not null)
        {
            SwitchState = LocaleSwitchState.Faulted;
            logger.Warning(
                "locale",
                "locale.switch_failed",
                $"Locale switch to '{normalized}' failed during preflight: {preflightFailure}",
                properties: new Dictionary<string, object?>
                {
                    ["previousLocale"] = previousLocale,
                    ["targetLocale"] = normalized,
                    ["reason"] = preflightFailure,
                });
            SwitchFailed?.Invoke(new LocaleSwitchFailedEvent(previousLocale, normalized, preflightFailure));
            SwitchState = LocaleSwitchState.Idle;
            return LocaleSwitchResult.Failed(previousLocale, normalized, preflightFailure);
        }

        if (preflightLocale is not null)
        {
            try
            {
                await preflightLocale(normalized, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return FailSwitch(previousLocale, normalized, exception.Message);
            }
        }

        var isBusy = isBusyCheck?.Invoke() == true;
        SwitchStarting?.Invoke(new LocaleSwitchStartingEvent(previousLocale, normalized, isBusy));

        if (isBusy)
        {
            SwitchState = LocaleSwitchState.PendingPresentationBoundary;
            if (waitForSafeBoundary is null)
            {
                return FailSwitch(
                    previousLocale,
                    normalized,
                    "Locale switch was requested during an unsafe runtime boundary, but no boundary waiter is configured.");
            }

            await waitForSafeBoundary(cancellationToken);
        }

        SwitchState = LocaleSwitchState.Applying;
        try
        {
            ApplyGodotEngineLocale(normalized);
            if (onLocaleApplied is not null)
            {
                await onLocaleApplied(normalized);
            }

            DialogueLocale = normalized;
        }
        catch (Exception exception)
        {
            await RollbackAppliedLocaleAsync(previousLocale);

            if (exception is OperationCanceledException)
            {
                SwitchState = LocaleSwitchState.Idle;
                throw;
            }

            return FailSwitch(previousLocale, normalized, exception.Message);
        }

        logger.Info(
            "locale",
            "locale.switched",
            $"Locale successfully switched to '{normalized}'.",
            properties: new Dictionary<string, object?>
            {
                ["previousLocale"] = previousLocale,
                ["currentLocale"] = normalized,
            });

        DialogueLocaleChanged?.Invoke(new LocaleChangedEvent(previousLocale, normalized));
        SwitchState = LocaleSwitchState.Idle;

        return LocaleSwitchResult.Succeeded(previousLocale, normalized);
    }

    public async Task<LocaleSwitchResult> ChangeVoiceLocaleAsync(
        string targetLocale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);
        var normalized = NormalizeLocale(targetLocale);
        await voiceSwitchGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(VoiceLocale, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return LocaleSwitchResult.Succeeded(VoiceLocale, VoiceLocale);
            }

            var previous = VoiceLocale;
            var failure = PreflightTargetLocale(normalized, voice: true);
            if (failure is not null)
            {
                return LocaleSwitchResult.Failed(previous, normalized, failure);
            }

            try
            {
                if (onVoiceLocaleApplied is not null)
                {
                    await onVoiceLocaleApplied(normalized);
                }
                VoiceLocale = normalized;
            }
            catch
            {
                if (onVoiceLocaleApplied is not null)
                {
                    try { await onVoiceLocaleApplied(previous); }
                    catch (Exception rollbackException)
                    {
                        logger.Error(
                            "locale",
                            "locale.voice_rollback_failed",
                            $"Voice locale rollback failed: {rollbackException.Message}",
                            rollbackException);
                    }
                }
                throw;
            }

            VoiceLocaleChanged?.Invoke(new LocaleChangedEvent(previous, normalized));
            return LocaleSwitchResult.Succeeded(previous, normalized);
        }
        finally
        {
            voiceSwitchGate.Release();
        }
    }

    private async Task RollbackAppliedLocaleAsync(string previousLocale)
    {
        try
        {
            ApplyGodotEngineLocale(previousLocale);
        }
        catch (Exception rollbackException)
        {
            logger.Error(
                "locale",
                "locale.switch_engine_rollback_failed",
                $"Locale rollback in Godot failed: {rollbackException.Message}",
                exception: rollbackException);
        }

        if (onLocaleApplied is null)
        {
            return;
        }

        try
        {
            await onLocaleApplied(previousLocale);
        }
        catch (Exception rollbackException)
        {
            logger.Error(
                "locale",
                "locale.switch_presentation_rollback_failed",
                $"Locale rollback presentation failed: {rollbackException.Message}",
                exception: rollbackException);
        }
    }

    private LocaleSwitchResult FailSwitch(string previousLocale, string targetLocale, string reason)
    {
        SwitchState = LocaleSwitchState.Faulted;
        logger.Warning(
            "locale",
            "locale.switch_failed",
            $"Locale switch to '{targetLocale}' failed: {reason}",
            properties: new Dictionary<string, object?>
            {
                ["previousLocale"] = previousLocale,
                ["targetLocale"] = targetLocale,
                ["reason"] = reason,
            });
        SwitchFailed?.Invoke(new LocaleSwitchFailedEvent(previousLocale, targetLocale, reason));
        SwitchState = LocaleSwitchState.Idle;
        return LocaleSwitchResult.Failed(previousLocale, targetLocale, reason);
    }

    private void RenewSwitchLifetime()
    {
        lock (sync)
        {
            var previous = switchLifetime;
            switchLifetime = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }
    }

    public void RecordMissingKey(string key, string locale)
    {
        Interlocked.Increment(ref missingKeyCount);
        logger.Warning(
            "locale",
            "locale.missing_key",
            $"Missing localization key '{key}' for locale '{locale}'.",
            properties: new Dictionary<string, object?>
            {
                ["key"] = key,
                ["locale"] = locale,
            });
    }

    public LocaleDiagnosticsSnapshot CaptureDiagnostics()
    {
        return new LocaleDiagnosticsSnapshot(
            DialogueLocale,
            VoiceLocale,
            DefaultLocale,
            DefaultVoiceLocale,
            AvailableLocales,
            GetDialogueFallbackChain(DialogueLocale),
            GetVoiceFallbackChain(VoiceLocale),
            SwitchState,
            missingKeyCount);
    }

    private string? PreflightTargetLocale(string targetLocale, bool voice = false)
    {
        lock (sync)
        {
            // If AvailableLocales is configured, target must be available or have a fallback chain
            if (availableLocales.Count > 0 &&
                !availableLocales.Contains(targetLocale, StringComparer.OrdinalIgnoreCase))
            {
                // Verify fallback chain reaches at least an available locale or default
                var chain = voice
                    ? GetVoiceFallbackChain(targetLocale)
                    : GetDialogueFallbackChain(targetLocale);
                if (!chain.Any(candidate => availableLocales.Contains(candidate, StringComparer.OrdinalIgnoreCase)))
                {
                    return $"Target locale '{targetLocale}' is not supported and has no supported fallback.";
                }
            }
        }

        return null;
    }

    private void ApplyGodotEngineLocale(string locale)
    {
        applyEngineLocale?.Invoke(locale);
    }

    public static string NormalizeLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        return locale.Trim().Replace('_', '-');
    }
}

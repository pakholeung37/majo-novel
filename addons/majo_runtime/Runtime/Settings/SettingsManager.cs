#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.State;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Settings;

public sealed class SettingsManager : ScopedStatePersistence, ISettingsManager
{
    private readonly IReadOnlyList<ISettingsProjection> projections;
    private readonly ILocaleManager? localeManager;
    private int localeChangeFromSettings;
    private bool localeEventSubscribed;

    public SettingsManager(
        RuntimeSettings defaults,
        IReadOnlyList<IStateContributor> contributors,
        IReadOnlyList<ISettingsProjection> projections,
        IStateSerializer serializer,
        IStateStorage storage,
        StateMigrationRegistry migrations,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        TimeSpan debounce,
        IRuntimeLogger? logger = null,
        ILocaleManager? localeManager = null)
        : base(
            StateScope.Settings,
            WithBuiltInContributor(contributors, out var builtIn, defaults.Validate()),
            serializer,
            storage,
            migrations,
            runtimeVersion,
            gameBuildId,
            storageNamespace,
            "scopes/settings",
            debounce,
            strictRecovery: false,
            logger)
    {
        Defaults = defaults;
        Current = defaults;
        this.projections = projections;
        this.localeManager = localeManager;
        builtIn.Attach(this);
    }

    public event Action<SettingsChanged>? Changed;
    public RuntimeSettings Defaults { get; }
    public RuntimeSettings Current { get; private set; }
    public string? LastProjectionError { get; private set; }

    public new event Action<Exception>? PersistenceFailed
    {
        add => base.PersistenceFailed += value;
        remove => base.PersistenceFailed -= value;
    }

    public void Update(RuntimeSettings settings)
    {
        settings.Validate();
        if (settings == Current)
        {
            return;
        }

        if (localeManager is not null &&
            (!string.Equals(localeManager.DialogueLocale, settings.DialogueLocale, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(localeManager.VoiceLocale, settings.VoiceLocale, StringComparison.OrdinalIgnoreCase)))
        {
            _ = ChangeLocalesAndCommitAsync(settings);
            return;
        }

        Commit(settings);
    }

    public void ResetToDefaults() => Update(Defaults);

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await base.InitializeAsync(cancellationToken);
        if (localeManager is not null &&
            !string.Equals(localeManager.DialogueLocale, Current.DialogueLocale, StringComparison.OrdinalIgnoreCase))
        {
            var result = await localeManager.ChangeDialogueLocaleAsync(Current.DialogueLocale, cancellationToken);
            if (!result.Success)
            {
                Current = Current with { DialogueLocale = localeManager.DialogueLocale };
                MarkDirty();
            }
        }

        if (localeManager is not null &&
            !string.Equals(localeManager.VoiceLocale, Current.VoiceLocale, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await localeManager.ChangeVoiceLocaleAsync(Current.VoiceLocale, cancellationToken);
            }
            catch
            {
                Current = Current with { VoiceLocale = localeManager.VoiceLocale };
                MarkDirty();
            }
        }

        if (localeManager is not null && !localeEventSubscribed)
        {
            localeManager.DialogueLocaleChanged += HandleDialogueLocaleChanged;
            localeManager.VoiceLocaleChanged += HandleVoiceLocaleChanged;
            localeEventSubscribed = true;
        }
        ApplyProjections();
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (localeManager is not null && localeEventSubscribed)
        {
            localeManager.DialogueLocaleChanged -= HandleDialogueLocaleChanged;
            localeManager.VoiceLocaleChanged -= HandleVoiceLocaleChanged;
            localeEventSubscribed = false;
        }
        await base.ShutdownAsync(cancellationToken);
    }

    public new Task FlushAsync(CancellationToken cancellationToken = default) =>
        base.FlushAsync(cancellationToken);

    internal void RestoreValue(RuntimeSettings settings)
    {
        Current = settings.Validate();
    }

    internal void ResetValue()
    {
        Current = Defaults;
    }

    private void ApplyProjections()
    {
        LastProjectionError = null;
        foreach (var projection in projections)
        {
            try
            {
                projection.Apply(Current);
            }
            catch (Exception exception)
            {
                LastProjectionError = exception.Message;
            }
        }
    }

    private void Commit(RuntimeSettings settings)
    {
        if (settings == Current)
        {
            return;
        }
        var previous = Current;
        Current = settings;
        ApplyProjections();
        MarkDirty();
        Changed?.Invoke(new SettingsChanged(previous, settings));
    }

    private async Task ChangeLocalesAndCommitAsync(RuntimeSettings settings)
    {
        Interlocked.Increment(ref localeChangeFromSettings);
        var previousDialogue = localeManager!.DialogueLocale;
        try
        {
            var dialogueResult = await localeManager.ChangeDialogueLocaleAsync(settings.DialogueLocale);
            if (!dialogueResult.Success)
            {
                LastProjectionError = dialogueResult.FailureReason;
                return;
            }

            try
            {
                var voiceResult = await localeManager.ChangeVoiceLocaleAsync(settings.VoiceLocale);
                if (!voiceResult.Success)
                {
                    await localeManager.ChangeDialogueLocaleAsync(previousDialogue);
                    LastProjectionError = voiceResult.FailureReason;
                    return;
                }
            }
            catch
            {
                await localeManager.ChangeDialogueLocaleAsync(previousDialogue);
                throw;
            }

            Commit(settings with
            {
                DialogueLocale = localeManager.DialogueLocale,
                VoiceLocale = localeManager.VoiceLocale,
            });
        }
        catch (Exception exception)
        {
            LastProjectionError = exception.Message;
        }
        finally
        {
            Interlocked.Decrement(ref localeChangeFromSettings);
        }
    }

    private void HandleDialogueLocaleChanged(LocaleChangedEvent change)
    {
        if (Volatile.Read(ref localeChangeFromSettings) > 0)
        {
            return;
        }

        Commit(Current with { DialogueLocale = change.CurrentLocale });
    }

    private void HandleVoiceLocaleChanged(LocaleChangedEvent change)
    {
        if (Volatile.Read(ref localeChangeFromSettings) == 0)
        {
            Commit(Current with { VoiceLocale = change.CurrentLocale });
        }
    }

    private static List<IStateContributor> WithBuiltInContributor(
        IReadOnlyList<IStateContributor> contributors,
        out RuntimeSettingsStateContributor builtIn,
        RuntimeSettings defaults)
    {
        _ = defaults;
        builtIn = new RuntimeSettingsStateContributor();
        var result = new List<IStateContributor>(contributors.Count + 1) { builtIn };
        result.AddRange(contributors);
        return result;
    }

    private sealed class RuntimeSettingsStateContributor : IImmediateStateContributor
    {
        private SettingsManager? manager;

        public string Key => "runtime.settings";
        public StateScope Scope => StateScope.Settings;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.Durable;
        public void Attach(SettingsManager value) => manager = value;
        public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StateRecord(
                Key, Scope, SchemaVersion, StateJson.Serialize(manager!.Current)));
        }
        public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateJson.Deserialize<RuntimeSettings>(record).Validate();
            return ValueTask.CompletedTask;
        }
        public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manager!.RestoreValue(StateJson.Deserialize<RuntimeSettings>(record));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manager is not null) manager.ResetValue();
            return ValueTask.CompletedTask;
        }
    }
}

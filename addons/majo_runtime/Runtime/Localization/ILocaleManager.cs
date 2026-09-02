#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Localization;

public interface ILocaleManager : IRuntimeService
{
    string DialogueLocale { get; }
    string VoiceLocale { get; }
    string DefaultLocale { get; }
    string DefaultVoiceLocale { get; }
    IReadOnlyList<string> AvailableLocales { get; }
    IReadOnlyList<string> GetDialogueFallbackChain(string locale);
    IReadOnlyList<string> GetVoiceFallbackChain(string locale);
    LocaleSwitchState SwitchState { get; }
    bool IsInitialized { get; }
    IFontCatalog Fonts { get; }

    event Action<LocaleSwitchStartingEvent>? SwitchStarting;
    event Action<LocaleChangedEvent>? DialogueLocaleChanged;
    event Action<LocaleChangedEvent>? VoiceLocaleChanged;
    event Action<LocaleSwitchFailedEvent>? SwitchFailed;

    Task<LocaleSwitchResult> ChangeDialogueLocaleAsync(string targetLocale, CancellationToken cancellationToken = default);
    Task<LocaleSwitchResult> ChangeVoiceLocaleAsync(string targetLocale, CancellationToken cancellationToken = default);
    LocaleDiagnosticsSnapshot CaptureDiagnostics();
}

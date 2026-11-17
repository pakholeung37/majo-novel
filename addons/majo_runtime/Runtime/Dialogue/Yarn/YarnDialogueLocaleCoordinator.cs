#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed class YarnDialogueLocaleCoordinator(
    ILocaleManager locales,
    IYarnDialogueBackend backend,
    IDialoguePlayer player,
    IDialoguePresentation presentation,
    IYarnCommandAdapter commands,
    Action<string> applyEngineLocale) : IRuntimeService, IDialogueLocaleSwitchParticipant
{
    private IDisposable? registration;
    public bool IsBusy => commands.HasActiveCommands;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        registration ??= locales.RegisterDialogueSwitchParticipant(this);
        await ApplyAsync(locales.DialogueLocale, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        registration?.Dispose();
        registration = null;
        return Task.CompletedTask;
    }

    public Task WaitForSafeBoundaryAsync(CancellationToken cancellationToken) =>
        backend.WaitForLocaleSwitchBoundaryAsync(cancellationToken);

    public Task ApplyAsync(string locale, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        applyEngineLocale(locale);
        backend.SetLocale(locale);
        if (player.State != DialogueSessionState.Running) return Task.CompletedTask;
        var chain = locales.GetDialogueFallbackChain(locale);
        if (player.IsPresentingOptions)
            presentation.ReRenderOptions(backend.ReResolveCurrentOptions(locale, chain));
        else if (!string.IsNullOrWhiteSpace(player.CurrentLineId) && backend.ReResolveCurrentLine(locale, chain) is { } line)
            presentation.ReRenderLine(line);
        return Task.CompletedTask;
    }
}

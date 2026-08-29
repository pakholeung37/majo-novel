#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Script;
using YarnSpinnerGodot;
using Majo.Runtime.Dialogue.Yarn.HotReload;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnDialogueBackend : IDialogueBackend,
    IScriptCatalogBackend,
    IScriptCheckpointBackend
{
    DialogueRunner Runner { get; }

    IDialogueVariableStore VariableStore { get; }

    void RegisterNativePresenter(Node presenter);

    void UnregisterNativePresenter(Node presenter);

    void SetPresentationQuiescenceWaiter(Func<CancellationToken, Task>? waiter);

    void ObserveCommandAdapter(IYarnCommandAdapter adapter);

    DialogueLine? ReResolveCurrentLine(string locale, IReadOnlyList<string> fallbackChain);

    IReadOnlyList<DialogueOption> ReResolveCurrentOptions(string locale, IReadOnlyList<string> fallbackChain);

    void SetLocale(string locale);

    Task WaitForLocaleSwitchBoundaryAsync(CancellationToken cancellationToken);

    Task ReplaceRunningProjectAsync(
        YarnProjectVersion version,
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin replayOrigin,
        CancellationToken cancellationToken);

    void SetHotReloadProgressionFrozen(bool frozen);
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Script;
using YarnSpinnerGodot;

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
}

#nullable enable

using Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnDialogueBackend : IDialogueBackend
{
    DialogueRunner Runner { get; }

    IDialogueVariableStore VariableStore { get; }

    void RegisterNativePresenter(Node presenter);

    void UnregisterNativePresenter(Node presenter);

    void ObserveCommandAdapter(IYarnCommandAdapter adapter);
}

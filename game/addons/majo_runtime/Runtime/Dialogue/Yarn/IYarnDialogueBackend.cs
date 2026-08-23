#nullable enable

using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnDialogueBackend : IDialogueBackend
{
    DialogueRunner Runner { get; }

    IDialogueVariableStore VariableStore { get; }

    void ObserveCommandAdapter(IYarnCommandAdapter adapter);
}

#nullable enable

using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Presentation;

public interface IDialoguePresentation : IRuntimeService
{
    bool IsLineActive { get; }

    bool IsLineDisplayComplete { get; }

    bool IsPresentingOptions { get; }
}

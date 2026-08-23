#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Presentation;

public interface IDialoguePresentation : IRuntimeService
{
    event Action? LineDisplayCompleted;

    event Action? LineDismissed;

    bool IsLineActive { get; }

    bool IsLineDisplayComplete { get; }

    bool IsPresentingOptions { get; }
}

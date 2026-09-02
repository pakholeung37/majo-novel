#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class FakeDialoguePresentation : IDialoguePresentation
{
    public event Action? LineDisplayCompleted;
    public event Action? LineDismissed;

    public bool IsLineActive { get; set; } = true;
    public bool IsLineDisplayComplete { get; set; } = true;
    public bool IsPresentingOptions { get; set; }

    public void ReRenderLine(DialogueLine line) { }
    public void ReRenderOptions(IReadOnlyList<DialogueOption> options) { }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void CompleteLine() => LineDisplayCompleted?.Invoke();
    public void DismissLine() => LineDismissed?.Invoke();
    public void FireDisplayCompleted() => LineDisplayCompleted?.Invoke();
    public void FireLineDismissed() => LineDismissed?.Invoke();
}

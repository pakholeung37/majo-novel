#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.Live2D;

/// <summary>Non-visual presenter used by the demo's end-to-end smoke mode.</summary>
public partial class Live2DDialogueProbe : Node, IDialoguePresenter
{
    public int LineCount { get; private set; }

    public Task OnDialogueStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        context.NextLineToken.ThrowIfCancellationRequested();
        LineCount++;
        return Task.CompletedTask;
    }

    public Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(options.FirstOrDefault(option => option.IsAvailable));
    }

    public Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void OnNodeEntered(string nodeName) => _ = nodeName;

    public void OnNodeExited(string nodeName) => _ = nodeName;
}

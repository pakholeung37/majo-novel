#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessDialoguePresenter : Node, IDialoguePresenter
{
    private readonly List<string> lines = new();

    public event Action<DialogueLine>? LinePresented;

    public IReadOnlyList<string> Lines => lines;

    public async Task PresentLineAsync(
        DialogueLine line,
        DialoguePresentationContext context)
    {
        context.NextLineToken.ThrowIfCancellationRequested();
        lines.Add(line.Text.Text);
        LinePresented?.Invoke(line);
        await Task.Yield();
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessDialoguePresenter : Node, DialoguePresenterBase
{
    private readonly List<string> lines = new();

    public event Action<LocalizedLine>? LinePresented;

    public IReadOnlyList<string> Lines => lines;

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public async YarnTask RunLineAsync(
        LocalizedLine line,
        LineCancellationToken token)
    {
        lines.Add(line.RawText ?? string.Empty);
        LinePresented?.Invoke(line);
        await YarnTask.Yield();
    }
}

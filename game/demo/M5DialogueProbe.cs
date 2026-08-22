#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Demo;

public sealed record M5DialogueProbeEvent(
    string Kind,
    string Value,
    ulong TimestampMilliseconds);

public partial class M5DialogueProbe : Node, DialoguePresenterBase
{
    private readonly List<M5DialogueProbeEvent> events = new();
    private readonly List<LocalizedLine> lines = new();

    public event Action<LocalizedLine>? LinePresented;

    public IReadOnlyList<M5DialogueProbeEvent> Events => events;

    public IReadOnlyList<LocalizedLine> Lines => lines;

    public int PreferredOptionIndex { get; set; }

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public YarnTask OnDialogueStartedAsync()
    {
        Record("dialogue", "started");
        return YarnTask.CompletedTask;
    }

    public async YarnTask RunLineAsync(
        LocalizedLine line,
        LineCancellationToken token)
    {
        token.NextLineToken.ThrowIfCancellationRequested();
        lines.Add(line);
        Record("line", line.TextID);
        LinePresented?.Invoke(line);
        await YarnTask.Yield();
    }

    public async YarnTask<DialogueOption?> RunOptionsAsync(
        DialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = dialogueOptions.Where(option => option.IsAvailable).ToArray();
        if (available.Length == 0)
        {
            Record("options", "none");
            return await DialogueRunner.NoOptionSelected;
        }

        var selectedIndex = Math.Clamp(PreferredOptionIndex, 0, available.Length - 1);
        var selected = available[selectedIndex];
        Record("option", selected.TextID);
        await YarnTask.Yield();
        return selected;
    }

    public YarnTask OnDialogueCompleteAsync()
    {
        Record("dialogue", "completed");
        return YarnTask.CompletedTask;
    }

    public void OnNodeEnter(string nodeName) => Record("node-enter", nodeName);

    public void OnNodeExit(string nodeName) => Record("node-exit", nodeName);

    private void Record(string kind, string value) => events.Add(
        new M5DialogueProbeEvent(kind, value, Time.GetTicksMsec()));
}

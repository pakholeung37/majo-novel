#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Demo;

public sealed record RuntimeDialogueProbeEvent(
    string Kind,
    string Value,
    ulong TimestampMilliseconds);

public partial class RuntimeDialogueProbe : Node, IDialoguePresenter
{
    private readonly List<RuntimeDialogueProbeEvent> events = new();
    private readonly List<DialogueLine> lines = new();

    public event Action<DialogueLine>? LinePresented;

    public IReadOnlyList<RuntimeDialogueProbeEvent> Events => events;

    public IReadOnlyList<DialogueLine> Lines => lines;

    public int PreferredOptionIndex { get; set; }

    public Task OnDialogueStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record("dialogue", "started");
        return Task.CompletedTask;
    }

    public async Task PresentLineAsync(
        DialogueLine line,
        DialoguePresentationContext context)
    {
        context.NextLineToken.ThrowIfCancellationRequested();
        lines.Add(line);
        Record("line", line.Id);
        LinePresented?.Invoke(line);
        await Task.Yield();
    }

    public async Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = options.Where(option => option.IsAvailable).ToArray();
        if (available.Length == 0)
        {
            Record("options", "none");
            return null;
        }

        var selectedIndex = Math.Clamp(PreferredOptionIndex, 0, available.Length - 1);
        var selected = available[selectedIndex];
        Record("option", selected.Line.Id);
        await Task.Yield();
        return selected;
    }

    public Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record("dialogue", "completed");
        return Task.CompletedTask;
    }

    public void OnNodeEntered(string nodeName) => Record("node-enter", nodeName);

    public void OnNodeExited(string nodeName) => Record("node-exit", nodeName);

    private void Record(string kind, string value) => events.Add(
        new RuntimeDialogueProbeEvent(kind, value, Time.GetTicksMsec()));
}

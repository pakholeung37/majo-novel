#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using YarnSpinnerGodot;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Yarn.Preloading;

public sealed record YarnPreloadReference(
    string Kind,
    string Id,
    string? Asset = null,
    string? Appearance = null,
    string? Stage = null);

/// <summary>Reads only straight-line instructions; never evaluates Yarn expressions.</summary>
public sealed class YarnPreloadPlanner
{
    private RuntimeCommandCatalog? commands;
    private readonly IRuntimeLogger logger;
    private global::Yarn.Node? node;
    private int cursor;

    public YarnPreloadPlanner(RuntimeCommandCatalog? commands = null, IRuntimeLogger? logger = null)
    {
        this.commands = commands;
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public void SetCommandCatalog(RuntimeCommandCatalog catalog) =>
        commands = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public IReadOnlyList<YarnPreloadReference> GetResources(IReadOnlyList<string> tokens)
    {
        if (commands is null) return Array.Empty<YarnPreloadReference>();
        try { return Resources(tokens, commands); }
        catch (Exception exception)
        {
            // Speculation must not stop playback, or discard the rest of the bounded window.
            logger.Warning("preload", "preload.command_analysis_failed",
                $"Could not describe resources for command '{string.Join(' ', tokens)}': {exception.Message}", exception);
            return Array.Empty<YarnPreloadReference>();
        }
    }

    internal static bool IsBeat(IReadOnlyList<string> tokens, string action) =>
        tokens.Count >= 2 && tokens[0].Equals("beat", StringComparison.OrdinalIgnoreCase) &&
        tokens[1].Equals(action, StringComparison.OrdinalIgnoreCase);

    public void Enter(global::Yarn.Node? value)
    {
        node = value;
        cursor = 0;
    }

    public void Invalidate() => cursor = -1;

    public void Line(string id)
    {
        var matches = node?.Instructions.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.RunLine?.LineID == id).ToArray();
        cursor = matches?.Length == 1 ? matches[0].index + 1 : -1;
    }

    public void Command(IReadOnlyList<string> tokens)
    {
        // Only advance an already known straight-line cursor. Duplicate commands
        // after a branch are not reliable anchors; the next unique line resyncs us.
        if (node is null || cursor < 0 || cursor >= node.Instructions.Count) return;
        var command = node.Instructions[cursor].RunCommand;
        cursor = command is not null && command.SubstitutionCount == 0 &&
            CommandsMatch(DialogueRunner.SplitCommandText(command.CommandText).ToArray(), tokens)
            ? cursor + 1 : -1;
    }

    private bool CommandsMatch(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (commands is null) return expected.SequenceEqual(actual);
        var first = commands.Resolve(expected, out var expectedInput);
        var second = commands.Resolve(actual, out var actualInput);
        return first is not null && ReferenceEquals(first, second) &&
            expectedInput!.RawArguments.SequenceEqual(actualInput!.RawArguments);
    }

    public IReadOnlyList<YarnPreloadReference> LookAhead(int voiceLines = 4, int maxInstructions = 128,
        Func<string, bool>? hasVoice = null)
    {
        var result = new List<YarnPreloadReference>();
        var actorDefinitions = new Dictionary<(string Stage, string Instance), string>();
        if (node is null || cursor < 0) return result;
        var lines = 0;
        var beats = 0;
        for (var index = cursor; index < node.Instructions.Count && index - cursor < maxInstructions; index++)
        {
            var instruction = node.Instructions[index];
            if (instruction.RunLine is { } line)
            {
                if (hasVoice is not null && !hasVoice(line.LineID)) continue;
                if (lines++ >= voiceLines) break;
                result.Add(new YarnPreloadReference("line", line.LineID));
            }
            else if (instruction.RunCommand is { SubstitutionCount: 0 } command)
            {
                var tokens = DialogueRunner.SplitCommandText(command.CommandText).ToArray();
                if (IsBeat(tokens, "begin") && beats++ > 0) break;
                var references = GetResources(tokens);
                foreach (var reference in references)
                {
                    if (reference.Kind is not ("character" or "object"))
                    {
                        result.Add(reference);
                        continue;
                    }
                    var key = (reference.Stage ?? "main", reference.Id);
                    var definition = reference.Asset;
                    if (definition is null) actorDefinitions.TryGetValue(key, out definition);
                    if (definition is not null) actorDefinitions[key] = definition;
                    result.Add(reference with { Asset = definition });
                }
            }
            else
            {
                // Includes options, jumps, function calls, substitutions and returns.
                break;
            }
        }
        // Repeated backdrop intents can reset an incremental appearance baseline.
        return result.ToArray();
    }

    public static IReadOnlyList<YarnPreloadReference> Resources(
        IReadOnlyList<string> tokens, RuntimeCommandCatalog commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands.DescribeResources(tokens).Select(reference => new YarnPreloadReference(
            reference.Kind, reference.Id, reference.Asset, reference.Appearance, reference.Stage)).ToArray();
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Preloading;

public sealed record YarnPreloadReference(string Kind, string Id);

/// <summary>Reads only straight-line instructions; never evaluates Yarn expressions.</summary>
public sealed class YarnPreloadPlanner
{
    private global::Yarn.Node? node;
    private int cursor;

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
            DialogueRunner.SplitCommandText(command.CommandText).SequenceEqual(tokens)
            ? cursor + 1 : -1;
    }

    public IReadOnlyList<YarnPreloadReference> LookAhead(int voiceLines = 4, int maxInstructions = 128,
        Func<string, bool>? hasVoice = null)
    {
        var result = new List<YarnPreloadReference>();
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
                if (tokens.Length >= 2 && tokens[0] == "beat" && tokens[1] == "begin" && beats++ > 0) break;
                result.AddRange(Resources(tokens));
            }
            else
            {
                // Includes options, jumps, function calls, substitutions and returns.
                break;
            }
        }
        return result.Distinct().ToArray();
    }

    public static IReadOnlyList<YarnPreloadReference> Resources(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1])) return Array.Empty<YarnPreloadReference>();
        var kind = tokens[0].ToLowerInvariant();
        if (kind is "backdrop" or "bgm" or "sfx" or "voice")
            return new[] { new YarnPreloadReference(kind, tokens[1]) };
        if (kind == "character")
        {
            var appearance = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("appearance:", StringComparison.OrdinalIgnoreCase));
            if (appearance is not null && appearance.Length > "appearance:".Length)
                return new[] { new YarnPreloadReference(kind, $"{tokens[1]}/{appearance["appearance:".Length..]}") };
        }
        return Array.Empty<YarnPreloadReference>();
    }
}

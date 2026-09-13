#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using YarnSpinnerGodot;

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
        var characterDefinitions = new Dictionary<(string Stage, string Instance), string>();
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
                var references = Resources(tokens);
                foreach (var reference in references)
                {
                    if (reference.Kind != "character")
                    {
                        result.Add(reference);
                        continue;
                    }
                    var key = (reference.Stage ?? "main", reference.Id);
                    var definition = reference.Asset;
                    if (definition is null) characterDefinitions.TryGetValue(key, out definition);
                    if (definition is not null) characterDefinitions[key] = definition;
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

    public static IReadOnlyList<YarnPreloadReference> Resources(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1])) return Array.Empty<YarnPreloadReference>();
        var kind = tokens[0].ToLowerInvariant();
        if ((kind is "character" or "object") && (tokens[1] is "move" or "fx" or "hide") ||
            kind == "backdrop" && tokens[1] == "fx") return Array.Empty<YarnPreloadReference>();
        if (kind == "object")
        {
            var asset = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("asset:", StringComparison.OrdinalIgnoreCase));
            var appearance = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("appearance:", StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
                return new[] { new YarnPreloadReference(kind, tokens[1], asset[6..], appearance?[11..] ?? "Default") };
        }
        if (kind is "bgm" or "sfx" or "voice")
            return new[] { new YarnPreloadReference(kind, tokens[1]) };
        if (kind is "character" or "backdrop")
        {
            var asset = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("asset:", StringComparison.OrdinalIgnoreCase));
            var appearance = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("appearance:", StringComparison.OrdinalIgnoreCase));
            var stage = tokens.Skip(2).FirstOrDefault(token => token.StartsWith("stage:", StringComparison.OrdinalIgnoreCase));
            return new[] { new YarnPreloadReference(
                kind,
                tokens[1],
                asset?["asset:".Length..],
                appearance?["appearance:".Length..],
                stage?["stage:".Length..]) };
        }
        return Array.Empty<YarnPreloadReference>();
    }
}

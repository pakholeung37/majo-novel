#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yarn.Compiler;

namespace Majo.Runtime.Dialogue.Yarn;

/// <summary>Shared editor/runtime source transform. Authored coordinates remain the diagnostic contract.</summary>
public sealed class YarnNodeEntryTransform
{
    public const string CommandName = "__majo_node_entry";
    private readonly Dictionary<string, int[]> sourceLines = new(StringComparer.Ordinal);

    public CompilationJob.File Transform(string fileName, string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder();
        var mapping = new List<int>();
        var header = false;
        var body = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            output.AppendLine(line);
            mapping.Add(index);
            if (body)
            {
                if (trimmed == "===") body = false;
                continue;
            }
            if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) header = true;
            if (header && trimmed == "---")
            {
                output.AppendLine($"<<{CommandName}>>");
                mapping.Add(index);
                body = true;
                header = false;
            }
        }
        sourceLines.Add(fileName, mapping.ToArray());
        return new CompilationJob.File { FileName = fileName, Source = output.ToString() };
    }

    public int OriginalLine(string file, int transformedLine) =>
        sourceLines.TryGetValue(file, out var lines) && transformedLine >= 0
            ? lines[Math.Min(transformedLine, lines.Length - 1)] : transformedLine;

    public void RestoreSourceCoordinates(CompilationResult result) =>
        Majo.YarnBridge.YarnCompilerSourceMap.Restore(result, OriginalLine);
}

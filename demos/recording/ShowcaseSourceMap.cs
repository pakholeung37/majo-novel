#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Dialogue.Yarn;
using Yarn;
using Yarn.Compiler;

namespace Majo.Demos.Recording;

public sealed record ShowcaseSourcePosition(string File, string Node, int Line);

/// <summary>Instruction-based mapping preserves repeated commands, branches, and subroutine returns.</summary>
public sealed class ShowcaseSourceMap
{
    private readonly Dictionary<(string Node, int Instruction), ShowcaseSourcePosition> positions = new();

    public ShowcaseSourceMap(IEnumerable<(string Path, string Content)> files, Library library,
        int languageVersion, Program expectedProgram)
    {
        var transform = new YarnNodeEntryTransform();
        var inputs = files.Select(file => (ISourceInput)transform.Transform(file.Path, file.Content)).ToArray();
        var job = CompilationJob.CreateFromInputs(inputs, library, languageVersion);
        var result = global::Yarn.Compiler.Compiler.Compile(job);
        transform.RestoreSourceCoordinates(result);
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0 || result.Program is null || result.ProjectDebugInfo is null)
            throw new InvalidOperationException("Cannot build recording source map: " + string.Join("\n", errors.Select(error => error.Message)));

        foreach (var node in expectedProgram.Nodes)
        {
            if (!result.Program.Nodes.TryGetValue(node.Key, out var compiled) ||
                !compiled.Instructions.SequenceEqual(node.Value.Instructions))
                throw new InvalidOperationException($"Recording source map differs from imported node '{node.Key}'. Reimport the Yarn project.");
            var debug = result.ProjectDebugInfo.GetNodeDebugInfo(node.Key);
            if (debug is null) continue;
            for (var index = 0; index < compiled.Instructions.Count; index++)
            {
                var info = debug.GetLineInfo(index);
                positions[(node.Key, index)] = new ShowcaseSourcePosition(
                    debug.FileName ?? throw new InvalidOperationException($"Missing source file for '{node.Key}'."),
                    node.Key, info.Range.Start.Line + 1);
            }
        }
    }

    public ShowcaseSourcePosition? Resolve(string node, int instruction) =>
        positions.GetValueOrDefault((node, instruction));
}

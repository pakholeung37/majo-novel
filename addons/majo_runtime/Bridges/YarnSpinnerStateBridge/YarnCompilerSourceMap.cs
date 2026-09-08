using Yarn.Compiler;

namespace Majo.YarnBridge;

/// <summary>Version-pinned compiler access, kept with the existing Yarn friend-assembly bridge.</summary>
public static class YarnCompilerSourceMap
{
    public static void Restore(CompilationResult result, Func<string, int, int> originalLine)
    {
        Yarn.Compiler.Range Map(string? file, Yarn.Compiler.Range range) => file is null ? range : new(
            originalLine(file, range.Start.Line), range.Start.Character,
            originalLine(file, range.End.Line), range.End.Character);
        foreach (var diagnostic in result.Diagnostics)
            diagnostic.Range = Map(diagnostic.FileName, diagnostic.Range);
        if (result.ProjectDebugInfo is { } debug)
            foreach (var node in debug.Nodes)
            {
                node.Range = Map(node.FileName, node.Range);
                foreach (var key in node.LineRanges.Keys.ToArray())
                    node.LineRanges[key] = Map(node.FileName, node.LineRanges[key]);
            }
        if (result.StringTable is { } strings)
            foreach (var key in strings.Keys.ToArray())
            {
                var info = strings[key];
                info.lineNumber = originalLine(info.fileName, info.lineNumber - 1) + 1;
                strings[key] = info;
            }
        foreach (var declaration in result.Declarations)
            declaration.Range = Map(declaration.SourceFileName, declaration.Range);
    }
}

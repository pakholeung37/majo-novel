#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Dialogue.Yarn.HotReload;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed record TypedDirectionResourceReference(
    DirectionResourceKind Kind,
    string SemanticId,
    string File,
    int Line,
    int Column);

public sealed record DirectionSourceMap(
    string Command,
    string File,
    int Line,
    int Column);

public sealed record DirectionImport(
    string Kind,
    string Alias,
    string SourceNode,
    string SourceSymbol,
    string File,
    int Line,
    int Column);

public sealed record DirectionMark(
    string Name, DirectionPoint Position, string File, int Line, int Column);

public sealed record DirectionPreloadManifest(
    IReadOnlyList<TypedDirectionResourceReference> References)
{
    public IReadOnlySet<DirectionResourceClaim> Claims =>
        References.Select(reference => new DirectionResourceClaim(reference.Kind, reference.SemanticId))
            .ToHashSet();

    public IReadOnlySet<string> SemanticIds =>
        References.Select(reference => reference.SemanticId)
            .ToHashSet(StringComparer.Ordinal);
}

public sealed record DirectionSymbolTable(
    IReadOnlySet<string> Stages,
    IReadOnlySet<string> Views,
    IReadOnlySet<string> ShotAliases,
    IReadOnlySet<string> Marks);

public sealed record NodeDirectionModule(
    string NodeId,
    long Version,
    DirectionSymbolTable Symbols,
    IReadOnlyList<TypedDirectionResourceReference> Resources,
    IReadOnlyList<DirectionSourceMap> SourceMaps,
    IReadOnlyList<DirectionImport> Imports,
    IReadOnlyList<DirectionMark> MarkDefinitions,
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics)
{
    public DirectionPreloadManifest PreloadManifest => new(Resources);
}

public sealed class NodeDirectionCompiler
{
    private static readonly IReadOnlySet<string> DirectionCommands = new HashSet<string>(
        new[] { "backdrop", "cg", "character", "leave", "camera", "shot", "layout",
            "import", "mark", "flash", "curtain", "beat" },
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex NodeHeader = new(
        "^title:\\s*(?<id>.+?)\\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Command = new(
        "<<(?<command>[^>]+)>>",
        RegexOptions.Compiled);
    private static readonly Regex Option = new(
        "(?<name>[a-zA-Z][\\w-]*):(?<value>\"[^\"]*\"|[^\\s]+)",
        RegexOptions.Compiled);
    private static readonly Regex Import = new(
        "^import\\s+(?<kind>stage|view)\\s+(?<alias>[\\w.-]+)\\s+from\\s+(?<node>[^:\\s]+):(?<symbol>[\\w.-]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<NodeDirectionModule> Compile(
        YarnSourceSnapshot snapshot,
        long version = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var modules = new List<NodeDirectionModule>();
        foreach (var file in snapshot.Files)
            CompileFile(file, version, modules);

        var compiled = modules.ToArray();
        foreach (var group in compiled
            .Select((module, index) => (module, index))
            .GroupBy(item => item.module.NodeId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                var location = item.module.SourceMaps.FirstOrDefault();
                var diagnostic = new YarnCompilationDiagnostic(
                    "error", "DIR006",
                    location?.File ?? snapshot.ManifestPath,
                    location?.Line ?? 1,
                    location?.Column ?? 1,
                    location?.Line ?? 1,
                    location?.Column ?? 1,
                    $"Node '{item.module.NodeId}' is defined more than once.");
                compiled[item.index] = item.module with
                {
                    Diagnostics = item.module.Diagnostics.Concat(new[] { diagnostic }).ToArray(),
                };
            }
        }

        var byNode = compiled
            .GroupBy(module => module.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        for (var index = 0; index < compiled.Length; index++)
        {
            var module = compiled[index];
            var diagnostics = new List<YarnCompilationDiagnostic>(module.Diagnostics);
            foreach (var import in module.Imports)
            {
                if (!byNode.TryGetValue(import.SourceNode, out var source) ||
                    !HasExportedSymbol(source, import.Kind, import.SourceSymbol))
                {
                    diagnostics.Add(new YarnCompilationDiagnostic(
                        "error", "DIR007", import.File, import.Line, import.Column,
                        import.Line, import.Column,
                        $"{import.Kind} import '{import.SourceNode}:{import.SourceSymbol}' " +
                        $"does not refer to a known symbol."));
                }
            }
            compiled[index] = module with { Diagnostics = diagnostics.ToArray() };
        }

        return compiled;
    }

    private static bool HasExportedSymbol(
        NodeDirectionModule module,
        string kind,
        string symbol) =>
        string.Equals(symbol, "main", StringComparison.Ordinal) ||
        (kind.Equals("stage", StringComparison.OrdinalIgnoreCase)
            ? module.Symbols.Stages.Contains(symbol) || module.Imports.Any(import =>
                import.Kind.Equals("stage", StringComparison.OrdinalIgnoreCase) &&
                import.Alias.Equals(symbol, StringComparison.Ordinal))
            : module.Symbols.Views.Contains(symbol) || module.Imports.Any(import =>
                import.Kind.Equals("view", StringComparison.OrdinalIgnoreCase) &&
                import.Alias.Equals(symbol, StringComparison.Ordinal)));

    private static void CompileFile(
        YarnSourceFileSnapshot file,
        long version,
        ICollection<NodeDirectionModule> modules)
    {
        string? node = null;
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var views = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<TypedDirectionResourceReference>();
        var maps = new List<DirectionSourceMap>();
        var imports = new List<DirectionImport>();
        var markDefinitions = new List<DirectionMark>();
        var shotReferences = new List<(string Alias, string File, int Line, int Column, int Length)>();
        var diagnostics = new List<YarnCompilationDiagnostic>();

        void Flush()
        {
            if (node is null) return;
            foreach (var reference in shotReferences)
            {
                if (aliases.Contains(reference.Alias)) continue;
                diagnostics.Add(new YarnCompilationDiagnostic(
                    "error", "DIR007", reference.File, reference.Line, reference.Column,
                    reference.Line, reference.Column + reference.Length,
                    $"Shot alias '{reference.Alias}' is not defined in Node '{node}'."));
            }
            modules.Add(new NodeDirectionModule(
                node,
                version,
                new DirectionSymbolTable(
                    new HashSet<string>(stages, StringComparer.Ordinal),
                    new HashSet<string>(views, StringComparer.Ordinal),
                    new HashSet<string>(aliases, StringComparer.Ordinal),
                    new HashSet<string>(marks, StringComparer.Ordinal)),
                resources.ToArray(),
                maps.ToArray(),
                imports.ToArray(),
                markDefinitions.ToArray(),
                diagnostics.ToArray()));
            stages.Clear();
            views.Clear();
            aliases.Clear();
            marks.Clear();
            resources.Clear();
            maps.Clear();
            imports.Clear();
            markDefinitions.Clear();
            shotReferences.Clear();
            diagnostics.Clear();
        }

        var lines = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var header = NodeHeader.Match(line);
            if (header.Success)
            {
                Flush();
                node = header.Groups["id"].Value.Trim();
                continue;
            }
            if (node is null) continue;
            foreach (Match match in Command.Matches(line))
            {
                var raw = match.Groups["command"].Value.Trim();
                var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (!DirectionCommands.Contains(tokens[0])) continue;
                var command = string.Join(' ', tokens.Take(2));
                maps.Add(new DirectionSourceMap(command, file.LogicalPath, index + 1, match.Index + 1));
                var import = Import.Match(raw);
                if (import.Success)
                {
                    var directionImport = new DirectionImport(
                        import.Groups["kind"].Value.ToLowerInvariant(),
                        import.Groups["alias"].Value,
                        import.Groups["node"].Value,
                        import.Groups["symbol"].Value,
                        file.LogicalPath,
                        index + 1,
                        match.Index + 1);
                    if (imports.Any(existing =>
                        existing.Alias.Equals(directionImport.Alias, StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "DIR005", file.LogicalPath, index + 1, match.Index + 1,
                            index + 1, match.Index + 1 + match.Length,
                            $"{directionImport.Kind} import alias '{directionImport.Alias}' is already defined in Node '{node}'."));
                    }
                    else
                    {
                        imports.Add(directionImport);
                    }
                    continue;
                }
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match option in Option.Matches(raw))
                {
                    var name = option.Groups["name"].Value;
                    if (!options.TryAdd(name, Unquote(option.Groups["value"].Value)))
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "DIR005", file.LogicalPath, index + 1,
                            match.Index + 1, index + 1, match.Index + 1 + match.Length,
                            $"Option '{name}' is specified more than once in Direction command."));
                    }
                }
                if (tokens[0].Equals("backdrop", StringComparison.OrdinalIgnoreCase))
                {
                    var resource = tokens.Length > 1 ? tokens[1] : string.Empty;
                    if (string.IsNullOrWhiteSpace(resource))
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "DIR001", file.LogicalPath, index + 1, match.Index + 1,
                            index + 1, match.Index + 1 + match.Length,
                            "backdrop requires a semantic resource id."));
                        continue;
                    }
                    resources.Add(new(DirectionResourceKind.Backdrop, Unquote(resource), file.LogicalPath, index + 1, match.Index + 1));
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                }
                else if (tokens[0].Equals("character", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                    if (TryOption(options, raw, "appearance", out var appearance) && tokens.Length > 1)
                        resources.Add(new(DirectionResourceKind.ActorAppearance, $"{tokens[1]}/{appearance}", file.LogicalPath, index + 1, match.Index + 1));
                }
                else if (tokens[0].Equals("leave", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                }
                else if (tokens[0].Equals("cg", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "view", out var view)) views.Add(view);
                    var resource = tokens.Length > 1 ? tokens[1] : string.Empty;
                    if (string.IsNullOrWhiteSpace(resource))
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "DIR002", file.LogicalPath, index + 1, match.Index + 1,
                            index + 1, match.Index + 1 + match.Length,
                            "cg requires a semantic resource id."));
                        continue;
                    }
                    resources.Add(new(DirectionResourceKind.Cg, Unquote(resource), file.LogicalPath, index + 1, match.Index + 1));
                }
                else if (tokens[0].Equals("camera", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "view", out var view)) views.Add(view);
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                    if (TryOption(options, raw, "as", out var alias))
                        AddSymbol(aliases, alias, "shot alias", file.LogicalPath, index + 1, match.Index + 1, match.Length);
                }
                else if (tokens[0].Equals("shot", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "view", out var view)) views.Add(view);
                    if (tokens.Length > 2 && tokens[1].Equals("use", StringComparison.OrdinalIgnoreCase))
                    {
                        shotReferences.Add((
                            Unquote(tokens[2]),
                            file.LogicalPath,
                            index + 1,
                            match.Index + 1,
                            match.Length));
                    }
                }
                else if (tokens[0].Equals("mark", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    var markName = tokens[1];
                    if (!marks.Add(markName))
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "DIR005", file.LogicalPath, index + 1, match.Index + 1,
                            index + 1, match.Index + 1 + match.Length,
                            $"mark '{markName}' is already defined in Node '{node}'."));
                        continue;
                    }
                    if (TryOption(options, raw, "at", out var at))
                    {
                        var parts = at.Split(',', StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 &&
                            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                            float.IsFinite(x) && float.IsFinite(y) && x is >= 0 and <= 100 && y is >= 0 and <= 100)
                        {
                            markDefinitions.Add(new DirectionMark(tokens[1], new DirectionPoint(x, y), file.LogicalPath, index + 1, match.Index + 1));
                        }
                        else
                        {
                            diagnostics.Add(new YarnCompilationDiagnostic("error", "DIR004", file.LogicalPath, index + 1, match.Index + 1, index + 1, match.Index + 1 + match.Length, "mark position must be finite x,y percentages between 0 and 100."));
                        }
                    }
                    else
                    {
                        diagnostics.Add(new YarnCompilationDiagnostic("error", "DIR003", file.LogicalPath, index + 1, match.Index + 1, index + 1, match.Index + 1 + match.Length, "mark requires an at:x,y position."));
                    }
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                }
                else if (tokens[0].Equals("layout", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "main", out var main)) views.Add(main);
                    if (TryOption(options, raw, "inset", out var inset)) views.Add(inset);
                }
            }
        }
        Flush();

        void AddSymbol(
            HashSet<string> symbols,
            string symbol,
            string kind,
            string logicalPath,
            int lineNumber,
            int column,
            int length)
        {
            if (symbols.Add(symbol)) return;
            diagnostics.Add(new YarnCompilationDiagnostic(
                "error", "DIR005", logicalPath, lineNumber, column,
                lineNumber, column + length,
                $"{kind} '{symbol}' is already defined."));
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static bool TryOption(
        IReadOnlyDictionary<string, string> options,
        string raw,
        string name,
        out string value)
    {
        if (options.TryGetValue(name, out value!)) return true;
        var marker = name + ":";
        var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            value = string.Empty;
            return false;
        }
        var start = index + marker.Length;
        var end = raw.IndexOfAny(new[] { ' ', '\t' }, start);
        value = Unquote((end < 0 ? raw[start..] : raw[start..end]).TrimEnd('>'));
        return !string.IsNullOrWhiteSpace(value);
    }
}

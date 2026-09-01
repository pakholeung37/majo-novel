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
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics)
{
    public DirectionPreloadManifest PreloadManifest => new(Resources);
}

public sealed class NodeDirectionCompiler
{
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
        return modules;
    }

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
        var diagnostics = new List<YarnCompilationDiagnostic>();

        void Flush()
        {
            if (node is null) return;
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
                diagnostics.ToArray()));
            stages.Clear();
            views.Clear();
            aliases.Clear();
            marks.Clear();
            resources.Clear();
            maps.Clear();
            imports.Clear();
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
                var command = string.Join(' ', tokens.Take(2));
                maps.Add(new DirectionSourceMap(command, file.LogicalPath, index + 1, match.Index + 1));
                var import = Import.Match(raw);
                if (import.Success)
                {
                    imports.Add(new DirectionImport(
                        import.Groups["kind"].Value.ToLowerInvariant(),
                        import.Groups["alias"].Value,
                        import.Groups["node"].Value,
                        import.Groups["symbol"].Value,
                        file.LogicalPath,
                        index + 1,
                        match.Index + 1));
                    continue;
                }
                var options = Option.Matches(raw).ToDictionary(
                    value => value.Groups["name"].Value,
                    value => Unquote(value.Groups["value"].Value),
                    StringComparer.OrdinalIgnoreCase);
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
                else if (tokens[0].Equals("place", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                    if (TryOption(options, raw, "look", out var look) && tokens.Length > 1)
                        resources.Add(new(DirectionResourceKind.ActorAppearance, $"{tokens[1]}/{look}", file.LogicalPath, index + 1, match.Index + 1));
                }
                else if (tokens[0].Equals("leave", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                }
                else if (tokens[0].Equals("shot", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryOption(options, raw, "view", out var view)) views.Add(view);
                    if (TryOption(options, raw, "stage", out var stage)) stages.Add(stage);
                    if (tokens.Length > 2 && tokens[1].Equals("cg", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(tokens[2]))
                        {
                            diagnostics.Add(new YarnCompilationDiagnostic(
                                "error", "DIR002", file.LogicalPath, index + 1, match.Index + 1,
                                index + 1, match.Index + 1 + match.Length,
                                "shot cg requires a semantic resource id."));
                            continue;
                        }
                        resources.Add(new(DirectionResourceKind.Cg, Unquote(tokens[2]), file.LogicalPath, index + 1, match.Index + 1));
                    }
                    if (TryOption(options, raw, "as", out var alias)) aliases.Add(alias);
                    if (tokens.Length > 2 && tokens[1].Equals("use", StringComparison.OrdinalIgnoreCase))
                        aliases.Add(Unquote(tokens[2]));
                }
                else if (tokens[0].Equals("mark", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    marks.Add(tokens[1]);
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

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
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
    private static readonly HashSet<string> DirectionCommands = new(
        new[] { "stage", "view", "backdrop", "character", "leave", "camera", "shot",
            "layout", "import", "mark", "curtain", "beat" },
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
    private static readonly char[] WhitespaceDelimiters = [' ', '\t'];

    public static IReadOnlyList<NodeDirectionModule> Compile(
        YarnSourceSnapshot snapshot,
        long version = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var modules = new List<NodeDirectionModule>();
        foreach (var file in snapshot.Files)
            CompileFile(file, version, modules);

        var compiled = modules.ToArray();
        AddDuplicateNodeDiagnostics(snapshot, compiled);
        ValidateImports(compiled);
        return compiled;
    }

    private static void CompileFile(
        YarnSourceFileSnapshot file,
        long version,
        List<NodeDirectionModule> modules)
    {
        string? node = null;
        var commands = new List<ParsedCommand>();
        var maps = new List<DirectionSourceMap>();
        var parseDiagnostics = new List<YarnCompilationDiagnostic>();

        void Flush()
        {
            if (node is null) return;
            modules.Add(CompileNode(
                node,
                version,
                commands,
                maps,
                parseDiagnostics));
            commands.Clear();
            maps.Clear();
            parseDiagnostics.Clear();
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
                if (tokens.Length == 0 || !DirectionCommands.Contains(tokens[0])) continue;
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match option in Option.Matches(raw))
                {
                    var name = option.Groups["name"].Value;
                    if (!options.TryAdd(name, Unquote(option.Groups["value"].Value)))
                    {
                        parseDiagnostics.Add(Diagnostic(
                            "DIR005",
                            file.LogicalPath,
                            index + 1,
                            match.Index + 1,
                            match.Length,
                            $"Option '{name}' is specified more than once in Direction command."));
                    }
                }
                var parsed = new ParsedCommand(
                    raw,
                    tokens,
                    options,
                    file.LogicalPath,
                    index + 1,
                    match.Index + 1,
                    match.Length);
                commands.Add(parsed);
                maps.Add(new DirectionSourceMap(
                    string.Join(' ', tokens.Take(2)),
                    file.LogicalPath,
                    index + 1,
                    match.Index + 1));
            }
        }
        Flush();
    }

    private static NodeDirectionModule CompileNode(
        string node,
        long version,
        IReadOnlyList<ParsedCommand> commands,
        IReadOnlyList<DirectionSourceMap> maps,
        IReadOnlyList<YarnCompilationDiagnostic> parseDiagnostics)
    {
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var views = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<TypedDirectionResourceReference>();
        var imports = new List<DirectionImport>();
        var markDefinitions = new List<DirectionMark>();
        var stageReferences = new List<SymbolReference>();
        var viewReferences = new List<SymbolReference>();
        var shotReferences = new List<SymbolReference>();
        var diagnostics = new List<YarnCompilationDiagnostic>(parseDiagnostics);

        foreach (var command in commands)
        {
            var import = Import.Match(command.Raw);
            if (import.Success)
            {
                var directionImport = new DirectionImport(
                    import.Groups["kind"].Value.ToLowerInvariant(),
                    import.Groups["alias"].Value,
                    import.Groups["node"].Value,
                    import.Groups["symbol"].Value,
                    command.File,
                    command.Line,
                    command.Column);
                if (imports.Any(existing => existing.Alias.Equals(
                        directionImport.Alias,
                        StringComparison.Ordinal)))
                {
                    diagnostics.Add(command.Diagnostic(
                        "DIR005",
                        $"Import alias '{directionImport.Alias}' is already defined in Node '{node}'."));
                }
                else
                {
                    imports.Add(directionImport);
                }
                continue;
            }

            if (command.Is("stage", "define"))
            {
                var name = command.Positional(2);
                if (!RequireName(command, name, "stage define requires an id.", diagnostics))
                    continue;
                if (name == StageId.Main.Value)
                {
                    diagnostics.Add(command.Diagnostic(
                        "DIR005",
                        "The built-in main Stage cannot be declared."));
                }
                else
                {
                    AddSymbol(stages, name, "Stage", command, diagnostics);
                }
            }
            else if (command.Is("view", "define"))
            {
                var name = command.Positional(2);
                if (!RequireName(command, name, "view define requires an id.", diagnostics))
                    continue;
                if (name == ViewId.Main.Value)
                {
                    diagnostics.Add(command.Diagnostic(
                        "DIR005",
                        "The built-in main View cannot be declared."));
                }
                else
                {
                    AddSymbol(views, name, "View", command, diagnostics);
                }
            }
            else if (command.Is("mark") && command.Tokens.Length > 1)
            {
                CompileMark(command, marks, markDefinitions, diagnostics);
            }
            else if (command.Is("camera") &&
                command.Options.TryGetValue("as", out var alias))
            {
                AddSymbol(aliases, alias, "Shot alias", command, diagnostics);
            }
        }

        foreach (var collision in stages.Intersect(views, StringComparer.Ordinal))
        {
            var command = commands.First(value =>
                (value.Is("stage", "define") || value.Is("view", "define")) &&
                value.Positional(2) == collision);
            diagnostics.Add(command.Diagnostic(
                "DIR005",
                $"'{collision}' cannot be both a Stage and an explicit View; the Stage already owns that default View name."));
        }

        foreach (var command in commands)
        {
            if (Import.IsMatch(command.Raw) || command.Is("mark")) continue;

            if (command.Is("stage"))
            {
                if (command.Is("stage", "show") || command.Is("stage", "clear") ||
                    command.Is("stage", "release"))
                {
                    AddReference(stageReferences, command.Positional(2), command);
                }
                continue;
            }
            if (command.Is("view"))
            {
                if (command.Is("view", "define"))
                {
                    if (command.Options.TryGetValue("stage", out var stage))
                        AddReference(stageReferences, stage, command);
                    else
                        diagnostics.Add(command.Diagnostic("DIR003", "view define requires stage:<id>."));
                }
                else if (command.Is("view", "release"))
                {
                    AddReference(viewReferences, command.Positional(2), command);
                }
                continue;
            }
            if (command.Is("backdrop"))
            {
                var resource = command.Positional(1);
                if (!RequireName(command, resource, "backdrop requires a semantic resource id.", diagnostics))
                    continue;
                resources.Add(new TypedDirectionResourceReference(
                    DirectionResourceKind.Backdrop,
                    Unquote(resource),
                    command.File,
                    command.Line,
                    command.Column));
                AddReference(stageReferences, command.OptionOr("stage", "main"), command);
            }
            else if (command.Is("character"))
            {
                AddReference(stageReferences, command.OptionOr("stage", "main"), command);
                if (command.Options.TryGetValue("appearance", out var appearance) &&
                    command.Tokens.Length > 1)
                {
                    resources.Add(new TypedDirectionResourceReference(
                        DirectionResourceKind.ActorAppearance,
                        $"{command.Tokens[1]}/{appearance}",
                        command.File,
                        command.Line,
                        command.Column));
                }
            }
            else if (command.Is("leave"))
            {
                AddReference(stageReferences, command.OptionOr("stage", "main"), command);
            }
            else if (command.Is("camera"))
            {
                AddCameraTargetReference(command, stageReferences, viewReferences, diagnostics);
            }
            else if (command.Is("shot", "use"))
            {
                AddReference(shotReferences, command.Positional(2), command);
                AddCameraTargetReference(command, stageReferences, viewReferences, diagnostics);
            }
            else if (command.Is("layout"))
            {
                AddReference(viewReferences, command.OptionOr("main", "main"), command);
                if (command.Options.TryGetValue("inset", out var inset))
                    AddReference(viewReferences, inset, command);
            }
        }

        foreach (var reference in stageReferences.Where(reference =>
                     !KnownStage(reference.Symbol, stages, imports)))
        {
            diagnostics.Add(reference.Diagnostic(
                "DIR007",
                $"Stage symbol '{reference.Symbol}' is not declared in Node '{node}'."));
        }
        foreach (var reference in viewReferences.Where(reference =>
                     !KnownView(reference.Symbol, stages, views, imports)))
        {
            diagnostics.Add(reference.Diagnostic(
                "DIR007",
                $"View symbol '{reference.Symbol}' is not declared in Node '{node}'."));
        }
        foreach (var reference in shotReferences.Where(reference =>
                     !aliases.Contains(reference.Symbol)))
        {
            diagnostics.Add(reference.Diagnostic(
                "DIR007",
                $"Shot alias '{reference.Symbol}' is not defined in Node '{node}'."));
        }

        return new NodeDirectionModule(
            node,
            version,
            new DirectionSymbolTable(stages, views, aliases, marks),
            resources,
            maps.ToArray(),
            imports,
            markDefinitions,
            diagnostics);
    }

    private static void CompileMark(
        ParsedCommand command,
        HashSet<string> marks,
        List<DirectionMark> definitions,
        List<YarnCompilationDiagnostic> diagnostics)
    {
        var name = command.Positional(1);
        if (!RequireName(command, name, "mark requires a name.", diagnostics)) return;
        if (!marks.Add(name))
        {
            diagnostics.Add(command.Diagnostic("DIR005", $"Mark '{name}' is already defined."));
            return;
        }
        if (!command.Options.TryGetValue("at", out var at))
        {
            diagnostics.Add(command.Diagnostic("DIR003", "mark requires an at:x,y position."));
            return;
        }
        var parts = at.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.IsFinite(x) || !float.IsFinite(y) ||
            x is < 0 or > 100 || y is < 0 or > 100)
        {
            diagnostics.Add(command.Diagnostic(
                "DIR004",
                "mark position must be finite x,y percentages between 0 and 100."));
            return;
        }
        definitions.Add(new DirectionMark(
            name,
            new DirectionPoint(x, y),
            command.File,
            command.Line,
            command.Column));
    }

    private static void AddCameraTargetReference(
        ParsedCommand command,
        List<SymbolReference> stageReferences,
        List<SymbolReference> viewReferences,
        List<YarnCompilationDiagnostic> diagnostics)
    {
        var hasStage = command.Options.TryGetValue("stage", out var stage);
        var hasView = command.Options.TryGetValue("view", out var view);
        if (hasStage && hasView)
        {
            diagnostics.Add(command.Diagnostic(
                "DIR005",
                "stage and view targets are mutually exclusive."));
            return;
        }
        if (hasStage) AddReference(stageReferences, stage!, command);
        else AddReference(viewReferences, hasView ? view! : "main", command);
    }

    private static bool KnownStage(
        string symbol,
        HashSet<string> stages,
        IReadOnlyList<DirectionImport> imports) =>
        symbol == StageId.Main.Value ||
        stages.Contains(symbol) ||
        imports.Any(import => import.Kind == "stage" && import.Alias == symbol);

    private static bool KnownView(
        string symbol,
        HashSet<string> stages,
        HashSet<string> views,
        IReadOnlyList<DirectionImport> imports) =>
        symbol == ViewId.Main.Value ||
        views.Contains(symbol) ||
        stages.Contains(symbol) ||
        imports.Any(import => import.Alias == symbol);

    private static void AddReference(
        List<SymbolReference> references,
        string symbol,
        ParsedCommand command)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        references.Add(new SymbolReference(
            Unquote(symbol),
            command.File,
            command.Line,
            command.Column,
            command.Length));
    }

    private static bool RequireName(
        ParsedCommand command,
        string value,
        string message,
        List<YarnCompilationDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(value)) return true;
        diagnostics.Add(command.Diagnostic("DIR001", message));
        return false;
    }

    private static void AddSymbol(
        HashSet<string> symbols,
        string symbol,
        string kind,
        ParsedCommand command,
        List<YarnCompilationDiagnostic> diagnostics)
    {
        if (symbols.Add(symbol)) return;
        diagnostics.Add(command.Diagnostic(
            "DIR005",
            $"{kind} '{symbol}' is already defined."));
    }

    private static void AddDuplicateNodeDiagnostics(
        YarnSourceSnapshot snapshot,
        NodeDirectionModule[] compiled)
    {
        foreach (var group in compiled
            .Select((module, index) => (module, index))
            .GroupBy(item => item.module.NodeId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                var location = item.module.SourceMaps.Count > 0 ? item.module.SourceMaps[0] : null;
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
    }

    private static void ValidateImports(NodeDirectionModule[] compiled)
    {
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
                        "does not refer to a known symbol."));
                }
            }
            compiled[index] = module with { Diagnostics = diagnostics };
        }
    }

    private static bool HasExportedSymbol(
        NodeDirectionModule module,
        string kind,
        string symbol) =>
        symbol == "main" ||
        (kind == "stage"
            ? module.Symbols.Stages.Contains(symbol) || module.Imports.Any(import =>
                import.Kind == "stage" && import.Alias == symbol)
            : module.Symbols.Views.Contains(symbol) || module.Imports.Any(import =>
                import.Kind == "view" && import.Alias == symbol));

    private static YarnCompilationDiagnostic Diagnostic(
        string code,
        string file,
        int line,
        int column,
        int length,
        string message) => new(
        "error", code, file, line, column, line, column + length, message);

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private sealed record ParsedCommand(
        string Raw,
        string[] Tokens,
        Dictionary<string, string> Options,
        string File,
        int Line,
        int Column,
        int Length)
    {
        public bool Is(string root) =>
            Tokens.Length > 0 && Tokens[0].Equals(root, StringComparison.OrdinalIgnoreCase);

        public bool Is(string root, string child) =>
            Tokens.Length > 1 && Is(root) &&
            Tokens[1].Equals(child, StringComparison.OrdinalIgnoreCase);

        public string Positional(int index) =>
            Tokens.Length > index ? Unquote(Tokens[index]) : string.Empty;

        public string OptionOr(string name, string fallback) =>
            Options.TryGetValue(name, out var value) ? value : fallback;

        public YarnCompilationDiagnostic Diagnostic(string code, string message) =>
            NodeDirectionCompiler.Diagnostic(code, File, Line, Column, Length, message);
    }

    private sealed record SymbolReference(
        string Symbol,
        string File,
        int Line,
        int Column,
        int Length)
    {
        public YarnCompilationDiagnostic Diagnostic(string code, string message) =>
            NodeDirectionCompiler.Diagnostic(code, File, Line, Column, Length, message);
    }
}

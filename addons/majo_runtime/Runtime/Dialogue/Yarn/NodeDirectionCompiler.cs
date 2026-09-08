#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Dialogue.Commands;
using System.Linq;
using System.Text.RegularExpressions;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
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

public sealed record DirectionCameraPreset(string Name, string Center, float Zoom, float Rotation);

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

public sealed record SceneEntryMetadata(
    string SceneId, string File, int Line, int Column);

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
    public NodeDirectionModule(
        string nodeId,
        long version,
        DirectionSymbolTable symbols,
        IReadOnlyList<TypedDirectionResourceReference> resources,
        IReadOnlyList<DirectionSourceMap> sourceMaps,
        IReadOnlyList<DirectionImport> imports,
        IReadOnlyList<DirectionMark> markDefinitions)
        : this(nodeId, version, symbols, resources, sourceMaps, imports, markDefinitions, Array.Empty<YarnCompilationDiagnostic>())
    {
    }

    public SceneEntryMetadata? SceneEntry { get; init; }
    public IReadOnlyList<DirectionCameraPreset> CameraPresets { get; init; } = Array.Empty<DirectionCameraPreset>();

    public DirectionPreloadManifest PreloadManifest => new(Resources);
}

public sealed class NodeDirectionCompiler
{
    private static readonly HashSet<string> DirectionCommands = new(
        new[] { "stage", "view", "backdrop", "character", "object", "camera", "shot",
            "screen", "import", "mark", "curtain", "beat" },
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

    public static IReadOnlyList<NodeDirectionModule> Compile(
        YarnSourceSnapshot snapshot,
        long version = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var modules = new List<NodeDirectionModule>();
        foreach (var file in snapshot.Files)
            CompileFile(file, version, modules);

        var entries = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];
            if (module.SceneEntry is { } entry && !entries.Add(entry.SceneId))
                modules[index] = module with
                {
                    Diagnostics = module.Diagnostics.Append(new YarnCompilationDiagnostic(
                        "error", "MAJO_SCENE", entry.File, entry.Line, entry.Column,
                        entry.Line, entry.Column + entry.SceneId.Length,
                        $"Scene '{entry.SceneId}' already has an entry node in this project.")).ToArray(),
                };
        }
        return modules;
    }

    private static void CompileFile(
        YarnSourceFileSnapshot file,
        long version,
        List<NodeDirectionModule> modules)
    {
        string? node = null;
        var inBody = false;
        SceneEntryMetadata? scene = null;
        var sceneHeaderSeen = false;
        var headerDiagnostics = new List<YarnCompilationDiagnostic>();
        var commands = new List<ParsedCommand>();
        var maps = new List<DirectionSourceMap>();

        void Flush()
        {
            if (node is null) return;
            var module = CompileNode(node, version, commands, maps);
            modules.Add(module with
            {
                SceneEntry = scene,
                Diagnostics = module.Diagnostics.Concat(headerDiagnostics).ToArray(),
            });
            scene = null;
            sceneHeaderSeen = false;
            inBody = false;
            headerDiagnostics.Clear();
            commands.Clear();
            maps.Clear();
        }

        var lines = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (inBody && line.Trim() == "===")
            {
                Flush();
                node = null;
                continue;
            }
            var header = NodeHeader.Match(inBody ? string.Empty : line);
            if (header.Success)
            {
                Flush();
                node = header.Groups["id"].Value.Trim();
                continue;
            }
            if (node is null) continue;
            if (!inBody)
            {
                if (line.Trim() == "---") inBody = true;
                else if (line.TrimStart().StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim();
                    if (sceneHeaderSeen || string.IsNullOrWhiteSpace(value))
                        headerDiagnostics.Add(new YarnCompilationDiagnostic(
                            "error", "MAJO_SCENE", file.LogicalPath, index + 1, 1,
                            index + 1, line.Length + 1,
                            sceneHeaderSeen ? "A node may declare scene only once." : "Scene id must not be empty."));
                    else scene = new SceneEntryMetadata(value, file.LogicalPath, index + 1, 1);
                    sceneHeaderSeen = true;
                }
                continue;
            }
            foreach (Match match in Command.Matches(line))
            {
                var raw = match.Groups["command"].Value.Trim();
                if (raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == YarnNodeEntryTransform.CommandName)
                    headerDiagnostics.Add(new YarnCompilationDiagnostic(
                        "error", "MAJO_RESERVED_COMMAND", file.LogicalPath, index + 1, match.Index + 1,
                        index + 1, match.Index + match.Length + 1,
                        $"'{YarnNodeEntryTransform.CommandName}' is reserved for the node-entry compiler."));
                var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || !DirectionCommands.Contains(tokens[0])) continue;
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match option in Option.Matches(raw))
                {
                    var name = option.Groups["name"].Value;
                    options.TryAdd(name, Unquote(option.Groups["value"].Value));
                }
                var parsed = new ParsedCommand(
                    raw,
                    tokens,
                    options,
                    file.LogicalPath,
                    index + 1,
                    match.Index + 1);
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
        IReadOnlyList<DirectionSourceMap> maps)
    {
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var views = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        var presets = new List<DirectionCameraPreset>();
        var resources = new List<TypedDirectionResourceReference>();
        var imports = new List<DirectionImport>();
        var markDefinitions = new List<DirectionMark>();
        var diagnostics = new List<YarnCompilationDiagnostic>();

        foreach (var command in commands)
        {
            var import = Import.Match(command.Raw);
            if (import.Success)
            {
                var alias = import.Groups["alias"].Value;
                if (!imports.Any(existing => existing.Alias.Equals(alias, StringComparison.Ordinal)))
                {
                    imports.Add(new DirectionImport(
                        import.Groups["kind"].Value.ToLowerInvariant(),
                        alias,
                        import.Groups["node"].Value,
                        import.Groups["symbol"].Value,
                        command.File,
                        command.Line,
                        command.Column));
                }
                continue;
            }

            if (command.Is("stage", "define"))
            {
                var name = command.Positional(2);
                if (!string.IsNullOrWhiteSpace(name) && name != StageId.Main.Value)
                {
                    stages.Add(name);
                }
            }
            else if (command.Is("view", "define"))
            {
                var name = command.Positional(2);
                if (!string.IsNullOrWhiteSpace(name) && name != ViewId.Main.Value)
                {
                    views.Add(name);
                }
            }
            else if (command.Is("mark") && command.Tokens.Length > 1)
            {
                CompileMark(command, marks, markDefinitions, diagnostics);
            }
            else if (command.Is("camera", "preset") && command.Positional(2) == "define")
            {
                try
                {
                    var name = command.Positional(3);
                    if (string.IsNullOrWhiteSpace(name)) throw new CommandValueFormatException("Camera preset requires a name.");
                    var center = command.Options.GetValueOrDefault("center", "50,50");
                    if (command.Raw.Contains('{')) throw new CommandValueFormatException("Camera preset definitions require literal parameters for deterministic restore.");
                    _ = PositionRefConverter.Instance.Parse(center);
                    var zoom = float.Parse(command.Options.GetValueOrDefault("zoom", "1"), System.Globalization.CultureInfo.InvariantCulture);
                    var rotation = float.Parse(command.Options.GetValueOrDefault("rotation", "0"), System.Globalization.CultureInfo.InvariantCulture);
                    if (!float.IsFinite(zoom) || zoom < 0.001f || !float.IsFinite(rotation)) throw new CommandValueFormatException("Invalid camera preset zoom or rotation.");
                    if (presets.Any(value => value.Name == name)) throw new CommandValueFormatException($"Duplicate camera preset '{name}'.");
                    presets.Add(new DirectionCameraPreset(name, center, zoom, rotation));
                    aliases.Add(name);
                }
                catch (Exception error) when (error is FormatException or OverflowException or CommandValueFormatException)
                {
                    diagnostics.Add(new YarnCompilationDiagnostic("error", "MAJO_CAMERA_PRESET", command.File,
                        command.Line, command.Column, command.Line, command.Column + command.Raw.Length, error.Message));
                }
            }
            else if (command.Is("camera") &&
                command.Options.TryGetValue("as", out var alias))
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    aliases.Add(alias);
                }
            }
            else if (command.Is("backdrop") && !command.Is("backdrop", "fx"))
            {
                var resource = command.Positional(1);
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    resources.Add(new TypedDirectionResourceReference(
                        DirectionResourceKind.Backdrop,
                        Unquote(resource),
                        command.File,
                        command.Line,
                        command.Column));
                }
            }
            else if (command.Is("object") && command.Options.TryGetValue("asset", out var asset))
            {
                resources.Add(new TypedDirectionResourceReference(DirectionResourceKind.ObjectDefinition,
                    Unquote(asset), command.File, command.Line, command.Column));
            }
            else if (command.Is("character") && !command.Is("character", "move") &&
                !command.Is("character", "hide") && !command.Is("character", "fx"))
            {
                if (command.Options.TryGetValue("appearance", out var appearance) &&
                    command.Tokens.Length > 1 &&
                    !string.IsNullOrWhiteSpace(appearance))
                {
                    resources.Add(new TypedDirectionResourceReference(
                        DirectionResourceKind.ActorAppearance,
                        $"{command.Tokens[1]}/{appearance}",
                        command.File,
                        command.Line,
                        command.Column));
                }
            }
        }

        return new NodeDirectionModule(
            node,
            version,
            new DirectionSymbolTable(stages, views, aliases, marks),
            resources,
            maps.ToArray(),
            imports,
            markDefinitions,
            diagnostics) { CameraPresets = presets };
    }

    private static void CompileMark(
        ParsedCommand command,
        HashSet<string> marks,
        List<DirectionMark> definitions,
        List<YarnCompilationDiagnostic> diagnostics)
    {
        var name = command.Positional(1);
        if (string.IsNullOrWhiteSpace(name) || !marks.Add(name)) return;
        if (!command.Options.TryGetValue("at", out var at)) return;
        // Yarn substitutions are evaluated before runtime command binding.
        if (at.Contains('{')) return;
        try
        {
            // Named references depend on project defaults and the executing node.
            // Only literals can be materialized in the compiled module.
            if (PositionRefConverter.Instance.Parse(at) is PositionRef.Literal literal)
                definitions.Add(new DirectionMark(
                    name, literal.Point, command.File, command.Line, command.Column));
        }
        catch (CommandValueFormatException error)
        {
            diagnostics.Add(new YarnCompilationDiagnostic(
                "error", "MAJO_POSITION", command.File, command.Line, command.Column,
                command.Line, command.Column + command.Raw.Length, error.Message));
        }
    }

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
        int Column)
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
    }
}

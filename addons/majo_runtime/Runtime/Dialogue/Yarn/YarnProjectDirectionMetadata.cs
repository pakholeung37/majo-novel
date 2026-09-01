#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using YarnProjectResource = YarnSpinnerGodot.YarnProject;

namespace YarnSpinnerGodot
{

// This partial keeps the serialized field on YarnProject itself, so imported
// projects retain Direction metadata even when hot reload is disabled. The
// metadata is intentionally JSON-only and does not make YarnSpinner depend on
// Majo runtime types at the resource layer.
public partial class YarnProject
{
    [Export] public string CompiledDirectionModulesJson = string.Empty;
}

}

namespace Majo.Runtime.Dialogue.Yarn
{

public static class YarnProjectDirectionMetadata
{
    public static string Serialize(IReadOnlyList<NodeDirectionModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return JsonSerializer.Serialize(modules.Select(ToPayload).ToArray());
    }

    public static IReadOnlyList<NodeDirectionModule> Deserialize(YarnProjectResource project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.CompiledDirectionModulesJson))
            return Array.Empty<NodeDirectionModule>();

        try
        {
            var payloads = JsonSerializer.Deserialize<ModulePayload[]>(
                project.CompiledDirectionModulesJson) ?? Array.Empty<ModulePayload>();
            return payloads.Select(FromPayload).ToArray();
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                "Could not read Direction metadata from a YarnProject: " + exception.Message);
            return Array.Empty<NodeDirectionModule>();
        }
    }

    private static ModulePayload ToPayload(NodeDirectionModule module) => new()
    {
        NodeId = module.NodeId,
        Version = module.Version,
        Symbols = new SymbolPayload
        {
            Stages = module.Symbols.Stages.ToArray(),
            Views = module.Symbols.Views.ToArray(),
            ShotAliases = module.Symbols.ShotAliases.ToArray(),
            Marks = module.Symbols.Marks.ToArray(),
        },
        Resources = module.Resources.Select(value => new ResourcePayload
        {
            Kind = value.Kind,
            SemanticId = value.SemanticId,
            File = value.File,
            Line = value.Line,
            Column = value.Column,
        }).ToArray(),
        SourceMaps = module.SourceMaps.Select(value => new SourceMapPayload
        {
            Command = value.Command,
            File = value.File,
            Line = value.Line,
            Column = value.Column,
        }).ToArray(),
        Imports = module.Imports.Select(value => new ImportPayload
        {
            Kind = value.Kind,
            Alias = value.Alias,
            SourceNode = value.SourceNode,
            SourceSymbol = value.SourceSymbol,
            File = value.File,
            Line = value.Line,
            Column = value.Column,
        }).ToArray(),
        Marks = module.MarkDefinitions.Select(value => new MarkPayload
        {
            Name = value.Name,
            X = value.Position.X,
            Y = value.Position.Y,
            File = value.File,
            Line = value.Line,
            Column = value.Column,
        }).ToArray(),
        Diagnostics = module.Diagnostics.ToArray(),
    };

    private static NodeDirectionModule FromPayload(ModulePayload value) => new(
        value.NodeId ?? throw new InvalidOperationException("Direction metadata has no node id."),
        value.Version,
        new DirectionSymbolTable(
            new HashSet<string>(value.Symbols?.Stages ?? Array.Empty<string>(), StringComparer.Ordinal),
            new HashSet<string>(value.Symbols?.Views ?? Array.Empty<string>(), StringComparer.Ordinal),
            new HashSet<string>(value.Symbols?.ShotAliases ?? Array.Empty<string>(), StringComparer.Ordinal),
            new HashSet<string>(value.Symbols?.Marks ?? Array.Empty<string>(), StringComparer.Ordinal)),
        (value.Resources ?? Array.Empty<ResourcePayload>()).Select(resource =>
            new TypedDirectionResourceReference(
                resource.Kind, resource.SemanticId ?? string.Empty, resource.File ?? string.Empty,
                resource.Line, resource.Column)).ToArray(),
        (value.SourceMaps ?? Array.Empty<SourceMapPayload>()).Select(map =>
            new DirectionSourceMap(
                map.Command ?? string.Empty, map.File ?? string.Empty, map.Line, map.Column)).ToArray(),
        (value.Imports ?? Array.Empty<ImportPayload>()).Select(import =>
            new DirectionImport(
                import.Kind ?? string.Empty, import.Alias ?? string.Empty,
                import.SourceNode ?? string.Empty, import.SourceSymbol ?? string.Empty,
                import.File ?? string.Empty, import.Line, import.Column)).ToArray(),
        (value.Marks ?? Array.Empty<MarkPayload>()).Select(mark =>
            new DirectionMark(
                mark.Name ?? string.Empty,
                new DirectionPoint(mark.X, mark.Y),
                mark.File ?? string.Empty, mark.Line, mark.Column)).ToArray(),
        value.Diagnostics ?? Array.Empty<YarnCompilationDiagnostic>());

    private sealed class ModulePayload
    {
        public string? NodeId { get; set; }
        public long Version { get; set; }
        public SymbolPayload? Symbols { get; set; }
        public ResourcePayload[]? Resources { get; set; }
        public SourceMapPayload[]? SourceMaps { get; set; }
        public ImportPayload[]? Imports { get; set; }
        public MarkPayload[]? Marks { get; set; }
        public YarnCompilationDiagnostic[]? Diagnostics { get; set; }
    }

    private sealed class SymbolPayload
    {
        public string[] Stages { get; set; } = Array.Empty<string>();
        public string[] Views { get; set; } = Array.Empty<string>();
        public string[] ShotAliases { get; set; } = Array.Empty<string>();
        public string[] Marks { get; set; } = Array.Empty<string>();
    }

    private sealed class ResourcePayload
    {
        public DirectionResourceKind Kind { get; set; }
        public string? SemanticId { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    private sealed class SourceMapPayload
    {
        public string? Command { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    private sealed class ImportPayload
    {
        public string? Kind { get; set; }
        public string? Alias { get; set; }
        public string? SourceNode { get; set; }
        public string? SourceSymbol { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    private sealed class MarkPayload
    {
        public string? Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }
}
}

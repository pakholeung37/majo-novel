#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

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

public sealed record DirectionCameraPreset(string Name, string Center, float Zoom, float Rotation, string Dolly = "neutral")
{
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

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

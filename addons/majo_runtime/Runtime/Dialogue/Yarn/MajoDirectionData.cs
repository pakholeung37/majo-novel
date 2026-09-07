#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

/// <summary>Majo-owned companion to an imported Yarn project, also used in exported games.</summary>
[Tool]
public partial class MajoDirectionData : Resource
{
    public const int CurrentSchemaVersion = 1;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public string ManifestPath { get; set; } = string.Empty;
    [Export] public string SourceFingerprint { get; set; } = string.Empty;
    [Export] public string ProgramFingerprint { get; set; } = string.Empty;
    [Export] public string ModulesJson { get; set; } = string.Empty;

    public static string GetPath(string manifestPath) => manifestPath + ".direction.tres";

    public static string HashProgram(YarnProject project) =>
        Convert.ToHexString(SHA256.HashData(project.CompiledYarnProgram)).ToLowerInvariant();

    public static IReadOnlyList<NodeDirectionModule> LoadModules(YarnProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // In-memory projects without source manifests have no imported companion.
        if (string.IsNullOrEmpty(project.JSONProjectPath))
            return Array.Empty<NodeDirectionModule>();

        var path = GetPath(project.JSONProjectPath);
        if (!ResourceLoader.Exists(path))
            throw new InvalidOperationException($"Missing Direction asset '{path}'. Reimport with the Majo Runtime editor plugin enabled.");
        using var data = ResourceLoader.Load<MajoDirectionData>(path, cacheMode: ResourceLoader.CacheMode.Ignore) ??
            throw new InvalidOperationException($"Could not load Direction asset '{path}'.");
        if (data.SchemaVersion != CurrentSchemaVersion ||
            data.ManifestPath != project.JSONProjectPath ||
            data.ProgramFingerprint != HashProgram(project))
            throw new InvalidOperationException($"Direction asset '{path}' is incompatible or stale. Reimport the Yarn project.");
        return YarnProjectDirectionMetadata.Deserialize(data.ModulesJson);
    }
}

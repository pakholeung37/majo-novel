#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Script;

public sealed record ScriptId
{
    public ScriptId(string projectId, string nodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ProjectId = projectId.Trim();
        NodeName = nodeName.Trim();
    }

    public string ProjectId { get; }

    public string NodeName { get; }

    public override string ToString() => $"{ProjectId}/{NodeName}";
}

public sealed record ScriptDescriptor
{
    public ScriptDescriptor(
        ScriptId id,
        string programFingerprint,
        IEnumerable<string> lineIds)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(programFingerprint);
        ProgramFingerprint = programFingerprint.Trim();
        ArgumentNullException.ThrowIfNull(lineIds);
        var normalized = lineIds.Select(lineId =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
            return lineId.Trim();
        }).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ScriptCheckpointException(
                $"Script '{id}' contains duplicate line IDs.");
        }

        LineIds = Array.AsReadOnly(normalized);
    }

    public ScriptId Id { get; }

    public string ProgramFingerprint { get; }

    public IReadOnlyList<string> LineIds { get; }
}

public enum ScriptPlaybackPhase
{
    Idle,
    Loading,
    Executing,
    PresentingLine,
    AwaitingAdvance,
    PresentingOptions,
    Stopping,
    Faulted,
}

public sealed record ScriptPlaybackPhaseChanged(
    ScriptPlaybackPhase Previous,
    ScriptPlaybackPhase Current);

public enum ScriptCheckpointBoundary
{
    AwaitingLineAdvance,
}

public sealed record ScriptPlaybackSpot
{
    public ScriptPlaybackSpot(
        ScriptId script,
        string lineId,
        ScriptCheckpointBoundary boundary = ScriptCheckpointBoundary.AwaitingLineAdvance)
    {
        Script = script ?? throw new ArgumentNullException(nameof(script));
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        LineId = lineId.Trim();
        Boundary = boundary;
    }

    public ScriptId Script { get; }

    public string LineId { get; }

    public ScriptCheckpointBoundary Boundary { get; }
}

public sealed record ScriptCheckpoint
{
    public const int CurrentFormatVersion = 1;

    public ScriptCheckpoint(
        int formatVersion,
        string programFingerprint,
        ScriptPlaybackSpot spot,
        IEnumerable<string>? resolvedSubstitutions = null)
    {
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(programFingerprint);
        FormatVersion = formatVersion;
        ProgramFingerprint = programFingerprint.Trim();
        Spot = spot ?? throw new ArgumentNullException(nameof(spot));
        ResolvedSubstitutions = Array.AsReadOnly(
            (resolvedSubstitutions ?? Array.Empty<string>())
                .Select(value => value ?? throw new ArgumentException(
                    "Resolved substitutions cannot contain null.",
                    nameof(resolvedSubstitutions)))
                .ToArray());
    }

    public int FormatVersion { get; }

    public string ProgramFingerprint { get; }

    public ScriptPlaybackSpot Spot { get; }

    public IReadOnlyList<string> ResolvedSubstitutions { get; }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public interface IYarnProjectVersionStore : IRuntimeService
{
    Task<YarnProject> GetActiveAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    YarnProjectVersion? GetActiveVersion(string projectId);

    YarnProjectVersion? GetRunningVersion(string projectId);

    YarnProjectVersion? GetLastKnownGood(string projectId);

    void StageForNextSession(YarnProjectVersion candidate);

    void Activate(YarnProjectVersion version);

    void MarkRunning(string projectId, YarnProject project);

    void UpdateSourceFingerprint(string projectId, string sourceFingerprint);
}

public sealed class YarnProjectVersionStore : IYarnProjectVersionStore
{
    private readonly IResourceLoader<YarnProject> importedProjects;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public YarnProjectVersionStore(IResourceManager resources, string scriptsPath)
    {
        ArgumentNullException.ThrowIfNull(resources);
        importedProjects = resources.CreateLoader<YarnProject>(scriptsPath);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            foreach (var entry in entries.Values)
            {
                entry.Running = null;
                if (entry.Candidate is not null)
                {
                    entry.Active = entry.Candidate;
                    entry.Candidate = null;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            entries.Clear();
        }
        return Task.CompletedTask;
    }

    public async Task<YarnProject> GetActiveAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (sync)
        {
            if (entries.TryGetValue(projectId, out var existing))
            {
                return existing.Active.Project;
            }
        }

        var imported = await importedProjects.LoadAsync(
            projectId,
            cancellationToken: cancellationToken);
        var version = CreateImportedVersion(projectId, imported);
        lock (sync)
        {
            if (!entries.TryGetValue(projectId, out var entry))
            {
                entry = new Entry(version);
                entries.Add(projectId, entry);
            }
            return entry.Active.Project;
        }
    }

    public YarnProjectVersion? GetActiveVersion(string projectId) => Get(projectId, e => e.Active);

    public YarnProjectVersion? GetRunningVersion(string projectId) => Get(projectId, e => e.Running);

    public YarnProjectVersion? GetLastKnownGood(string projectId) => Get(projectId, e => e.LastKnownGood);

    public void StageForNextSession(YarnProjectVersion candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            if (entries.TryGetValue(candidate.ProjectId, out var entry))
            {
                if (entry.Running is null)
                {
                    entry.Active = candidate;
                    entry.Candidate = null;
                }
                else
                {
                    entry.Candidate = candidate;
                }
            }
            else
            {
                entries.Add(candidate.ProjectId, new Entry(candidate));
            }
        }
    }

    public void Activate(YarnProjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        lock (sync)
        {
            if (!entries.TryGetValue(version.ProjectId, out var entry))
            {
                entries.Add(version.ProjectId, new Entry(version));
                return;
            }
            entry.Active = version;
            if (ReferenceEquals(entry.Candidate?.Project, version.Project))
            {
                entry.Candidate = null;
            }
        }
    }

    public void MarkRunning(string projectId, YarnProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(project);
        lock (sync)
        {
            if (!entries.TryGetValue(projectId, out var entry) ||
                !ReferenceEquals(entry.Active.Project, project))
            {
                throw new InvalidOperationException(
                    $"Yarn project '{projectId}' is not the active version.");
            }
            entry.Running = entry.Active;
            entry.LastKnownGood = entry.Active;
            entry.Candidate = null;
        }
    }

    public void UpdateSourceFingerprint(string projectId, string sourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        lock (sync)
        {
            if (entries.TryGetValue(projectId, out var entry))
            {
                entry.Active = entry.Active with { SourceFingerprint = sourceFingerprint };
                if (entry.Running is not null)
                {
                    entry.Running = entry.Running with { SourceFingerprint = sourceFingerprint };
                }
                entry.LastKnownGood = entry.LastKnownGood with { SourceFingerprint = sourceFingerprint };
                if (entry.Candidate is not null)
                {
                    entry.Candidate = entry.Candidate with { SourceFingerprint = sourceFingerprint };
                }
            }
        }
    }


    private YarnProjectVersion? Get(
        string projectId,
        Func<Entry, YarnProjectVersion?> selector)
    {
        lock (sync)
        {
            return entries.TryGetValue(projectId, out var entry) ? selector(entry) : null;
        }
    }

    private static YarnProjectVersion CreateImportedVersion(string projectId, YarnProject project)
    {
        var nodes = new HashSet<string>(project.Program.Nodes.Keys, StringComparer.Ordinal);
        var lineNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var nodeLineIds = project.Program.LineIDsForNode(node);
            if (nodeLineIds is null)
            {
                continue;
            }
            foreach (var lineId in nodeLineIds)
            {
                lineNodes.TryAdd(lineId, node);
            }
        }
        var bytes = project.CompiledYarnProgram;
        return new YarnProjectVersion(
            0,
            projectId,
            string.Empty,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            project,
            nodes,
            new HashSet<string>(lineNodes.Keys, StringComparer.Ordinal),
            lineNodes,
            project.LastImportHadImplicitStringIDs
                ? new HashSet<string>(project.baseLocalization?.GetLineIDs() ?? Array.Empty<string>(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            Array.Empty<YarnCompilationDiagnostic>(),
            DateTimeOffset.MinValue)
        {
            DirectionModules = YarnProjectDirectionMetadata.Deserialize(project),
        };
    }

    private sealed class Entry
    {
        public Entry(YarnProjectVersion imported)
        {
            Imported = imported;
            Active = imported;
            LastKnownGood = imported;
        }

        public YarnProjectVersion Imported { get; }
        public YarnProjectVersion Active { get; set; }
        public YarnProjectVersion? Running { get; set; }
        public YarnProjectVersion LastKnownGood { get; set; }
        public YarnProjectVersion? Candidate { get; set; }
    }
}

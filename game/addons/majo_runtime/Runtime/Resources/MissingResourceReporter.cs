#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

public sealed record MissingResourceOccurrence(
    string ResourceType,
    string ResourceId,
    string Fallback,
    string? RequestedPath,
    int Count);

public interface IMissingResourceReporter : IRuntimeService
{
    IReadOnlyList<MissingResourceOccurrence> Occurrences { get; }

    void Report(
        string resourceType,
        string resourceId,
        string fallback,
        string? requestedPath = null);
}

public sealed class MissingResourceReporter : IMissingResourceReporter
{
    private readonly object gate = new();
    private readonly Dictionary<OccurrenceKey, int> occurrences = new();
    private readonly IRuntimeLogger logger;

    public MissingResourceReporter(IRuntimeLogger? logger = null) =>
        this.logger = logger ?? NullRuntimeLogger.Instance;

    public IReadOnlyList<MissingResourceOccurrence> Occurrences
    {
        get
        {
            lock (gate)
            {
                return new ReadOnlyCollection<MissingResourceOccurrence>(occurrences
                    .Select(entry => new MissingResourceOccurrence(
                        entry.Key.ResourceType,
                        entry.Key.ResourceId,
                        entry.Key.Fallback,
                        entry.Key.RequestedPath,
                        entry.Value))
                    .OrderBy(entry => entry.ResourceType, StringComparer.Ordinal)
                    .ThenBy(entry => entry.ResourceId, StringComparer.Ordinal)
                    .ToArray());
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            occurrences.Clear();
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Report(
        string resourceType,
        string resourceId,
        string fallback,
        string? requestedPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var key = new OccurrenceKey(
            resourceType.Trim(),
            resourceId.Trim(),
            fallback.Trim(),
            string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath.Trim());
        var firstOccurrence = false;
        lock (gate)
        {
            occurrences.TryGetValue(key, out var count);
            occurrences[key] = count + 1;
            firstOccurrence = count == 0;
        }

        if (!firstOccurrence)
        {
            return;
        }

        logger.Warning(
            "resource",
            "resource.missing_fallback",
            $"Missing {key.ResourceType} resource '{key.ResourceId}'; fallback '{key.Fallback}' was used.",
            properties: new Dictionary<string, object?>
            {
                ["resourceType"] = key.ResourceType,
                ["resourceId"] = key.ResourceId,
                ["requestedPath"] = key.RequestedPath,
                ["fallback"] = key.Fallback,
            });
    }

    private readonly record struct OccurrenceKey(
        string ResourceType,
        string ResourceId,
        string Fallback,
        string? RequestedPath);
}

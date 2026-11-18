#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Direction.Execution;

public enum ScenePreparationReason { Enter, Restore }

public sealed record ScenePreparationContext(
    string? ProjectId, string SceneId, ScenePreparationReason Reason, IResourceManager Resources);

/// <summary>Developer-owned setup awaited before scene commands or restored rendering use its resources.
/// Hooks must be repeatable, and must not start dialogue or mutate presentation state.</summary>
public sealed class ScenePreparationRegistry : IRuntimeService
{
    private readonly Dictionary<string, Func<ScenePreparationContext, CancellationToken, Task>> handlers = new(StringComparer.Ordinal);
    private readonly IResourceManager resources;
    public ScenePreparationRegistry(IResourceManager resources) => this.resources = resources;

    public ScenePreparationRegistry Register(string sceneId, Func<ScenePreparationContext, CancellationToken, Task> prepare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(prepare);
        if (!handlers.TryAdd(sceneId, prepare))
            throw new ArgumentException($"Scene '{sceneId}' already has a preparation hook.", nameof(sceneId));
        return this;
    }

    public Task PrepareAsync(string? projectId, string? sceneId, ScenePreparationReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sceneId is not null && handlers.TryGetValue(sceneId, out var prepare)
            ? prepare(new ScenePreparationContext(projectId, sceneId, reason, resources), cancellationToken)
            : Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

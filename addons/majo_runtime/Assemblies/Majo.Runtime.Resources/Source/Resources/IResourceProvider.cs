#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

public sealed record ResourceRequest(
    ResourceKey Key,
    Type ResourceType,
    string? OverridePath = null);

public sealed record ResourceLocation(
    string ProviderId,
    string Path);

public interface IResourceProvider
{
    string Id { get; }

    IReadOnlyList<string> GetCandidatePaths(ResourceRequest request);

    bool TryResolve(ResourceRequest request, out ResourceLocation? location);

    object Load(ResourceLocation location, Type resourceType);

    Task<object> LoadAsync(
        ResourceLocation location,
        Type resourceType,
        CancellationToken cancellationToken);

    Task InitializeAsync(CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors;

public interface IActorFactory
{
    string FactoryId { get; }
    bool PreloadExistingAppearances => true;
    string? GetInitialAppearance(ActorDefinition definition) => null;
    IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition,
        string appearance) => Array.Empty<ResourcePreloadEntry>();
    Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken);
    void ValidateDefinition(ActorDefinition definition) { }
}

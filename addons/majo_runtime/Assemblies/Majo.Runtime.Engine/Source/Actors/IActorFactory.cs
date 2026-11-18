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
    /// <summary>Whether the authored appearance needs a previous full appearance.
    /// Preload callers skip such input when that baseline is not known.</summary>
    bool IsIncrementalAppearance(string appearance) => false;
    /// <summary>Pure metadata operation returning a complete appearance. Factories
    /// supporting incremental appearance syntax override this without creating nodes.</summary>
    string ResolveAppearance(ActorDefinition definition, string sourceAppearance, string appearance) => appearance;
    IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition,
        string appearance) => Array.Empty<ResourcePreloadEntry>();
    Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken);
    void ValidateDefinition(ActorDefinition definition) { }
}

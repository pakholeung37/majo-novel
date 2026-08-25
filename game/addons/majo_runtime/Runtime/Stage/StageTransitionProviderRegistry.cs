#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Stage;

public sealed class StageTransitionProviderRegistry
{
    private readonly Dictionary<string, IStageTransitionProvider> providers =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<IStageTransitionProvider> Providers => providers.Values;

    public StageTransitionProviderRegistry Register(
        IStageTransitionProvider provider,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
        if (!replace && providers.ContainsKey(provider.Id))
            throw new InvalidOperationException(
                $"Stage transition provider '{provider.Id}' is already registered.");
        providers[provider.Id] = provider;
        return this;
    }
}

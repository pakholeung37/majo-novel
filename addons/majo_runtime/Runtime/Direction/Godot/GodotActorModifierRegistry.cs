#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Actors;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotActorModifierRegistry
{
    private readonly Dictionary<ActorFxTarget, IActorModifierTarget> targets = new();
    private readonly Dictionary<ActorFxTarget, long> versions = new();
    private long versionCounter;

    internal Func<ActorFxTarget, Task>? ReleaseActorEffectsAsync { get; set; }

    internal IActorModifierTarget? Find(ActorFxTarget target) => targets.GetValueOrDefault(target);

    internal void Register(ActorFxTarget target, IActorModifierTarget binding)
    {
        targets[target] = binding;
        versions[target] = ++versionCounter;
    }

    internal async Task ReleaseAsync(ActorFxTarget target, IActorModifierTarget binding)
    {
        if (targets.GetValueOrDefault(target) != binding)
        {
            return;
        }

        var version = versions.GetValueOrDefault(target);
        if (ReleaseActorEffectsAsync is { } release)
        {
            await release(target);
        }

        if (versions.GetValueOrDefault(target) == version)
        {
            targets.Remove(target);
            versions.Remove(target);
        }
    }

    internal void Clear()
    {
        targets.Clear();
        versions.Clear();
    }
}

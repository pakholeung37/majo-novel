#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Demos.AceAttorney.Actors;

public sealed class SpriteSheetActorRegistry : IRuntimeService
{
    private readonly Dictionary<string, SpriteSheetCharacterActor> actors = new(StringComparer.Ordinal);

    public void Register(SpriteSheetCharacterActor actor) => actors[actor.Id] = actor;

    public void Unregister(SpriteSheetCharacterActor actor)
    {
        if (actors.TryGetValue(actor.Id, out var current) && ReferenceEquals(current, actor))
            actors.Remove(actor.Id);
    }

    public void SetSpeaking(string id, bool speaking)
    {
        if (actors.TryGetValue(id, out var actor)) actor.SetSpeaking(speaking);
    }

    public void StopAll()
    {
        foreach (var actor in actors.Values) actor.SetSpeaking(false);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        actors.Clear();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => ResetAsync(cancellationToken);
}

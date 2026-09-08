#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Actors;

public interface IActorModifier
{
    string EffectName { get; }
    void Validate(ActorFxCue cue);
    Task ExecuteAsync(ActorFxCue cue, Action<ActorVisualDelta> apply, IFrameClock clock, CancellationToken token);
}

public sealed class ShakeActorModifier : IActorModifier
{
    public string EffectName => "shake";
    public void Validate(ActorFxCue cue) => cue.Validate();
    public async Task ExecuteAsync(ActorFxCue cue, Action<ActorVisualDelta> apply, IFrameClock clock, CancellationToken token)
    {
        if (cue.DurationSeconds == 0) return;
        var samples = Math.Max(1, (int)Math.Ceiling(cue.DurationSeconds * cue.Frequency));
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        for (var index = 0; index < samples; index++)
        {
            token.ThrowIfCancellationRequested();
            var decay = 1f - (float)index / samples;
            var amount = cue.Strength * decay * decay * (index % 2 == 0 ? 1 : -1);
            var offset = cue.Axis switch
            {
                ActorShakeAxis.X => new Vector2(amount, 0),
                ActorShakeAxis.Y => new Vector2(0, amount),
                ActorShakeAxis.Diagonal => new Vector2(amount, amount),
                _ => new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * amount,
            };
            apply(ActorVisualDelta.Identity with { Offset = offset });
            await clock.DelayAsync(TimeSpan.FromSeconds(cue.DurationSeconds / samples), token);
        }
    }
}

public sealed class ActorModifierRegistry
{
    private readonly Dictionary<string, IActorModifier> modifiers = new(StringComparer.OrdinalIgnoreCase);
    private bool sealedRegistry;
    public ActorModifierRegistry() => Register(new ShakeActorModifier());
    public void Register(IActorModifier modifier)
    {
        if (sealedRegistry) throw new InvalidOperationException("Actor modifier registry is sealed.");
        modifiers.Add(modifier.EffectName, modifier);
    }
    public void Seal() => sealedRegistry = true;
    public IActorModifier Resolve(string effect) => modifiers.TryGetValue(effect, out var value)
        ? value : throw new DirectionException($"Unknown Actor modifier '{effect}'.");
}

public interface IActorModifierManager : IRuntimeService
{
    IOperationHandle ApplyEffect(ActorFxCue cue);
    Task ClearTargetAsync(ActorFxTarget target, CancellationToken cancellationToken = default);
}

public sealed class ActorModifierManager : IActorModifierManager
{
    private readonly ActorModifierRegistry registry;
    private readonly ModifierOperationRunner<ActorFxTarget, ActorVisualDelta> operations;
    private readonly Func<ActorFxTarget, IActorModifierTarget?> targets;

    public ActorModifierManager(Func<ActorFxTarget, IActorModifierTarget?> targets, IOperationRunner runner,
        IDirectionAccessibility accessibility, IFrameClock clock, ActorModifierRegistry? registry = null)
    {
        this.targets = targets;
        this.registry = registry ?? new ActorModifierRegistry();
        operations = new(runner, accessibility, clock, ActorVisualDelta.Identity,
            (left, right) => left.Combine(right), (target, delta) => targets(target)?.ApplyDelta(delta));
    }
    public IOperationHandle ApplyEffect(ActorFxCue cue)
    {
        var modifier = registry.Resolve(cue.EffectName);
        modifier.Validate(cue);
        // A duration-zero hide in the same beat has already released its presentation.
        if (targets(cue.Target) is null) return IOperationHandle.Completed;
        return operations.Start(cue.Target, cue.DurationSeconds, (apply, clock, token) => modifier.ExecuteAsync(cue, apply, clock, token));
    }
    public Task ClearTargetAsync(ActorFxTarget target, CancellationToken cancellationToken = default) => operations.ClearTargetAsync(target, cancellationToken);
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => operations.ResetAsync(cancellationToken);
    public Task ShutdownAsync(CancellationToken cancellationToken) => ResetAsync(cancellationToken);
}

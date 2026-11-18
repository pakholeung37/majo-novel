#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal sealed class AudioTransitionCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, AudioTransition> bgmTransitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioTransition> sfxTransitions = new(StringComparer.Ordinal);
    private AudioTransition? voiceTransition;
    private readonly IRuntimeActivityRegistry? activities;

    public AudioTransitionCoordinator(IRuntimeActivityRegistry? activities = null)
    {
        this.activities = activities;
    }

    public AudioTransition StartFade(
        IReadOnlyList<AudioFadeTarget> targets,
        AudioFade fade,
        Action? complete,
        Action? cancel)
    {
        return new AudioTransition(targets, fade, activities, complete, cancel);
    }

    public bool IsFading(string instanceId)
    {
        lock (gate)
        {
            if (bgmTransitions.TryGetValue(instanceId, out var bgm) && !bgm.Settlement.IsCompleted)
            {
                return true;
            }
            if (sfxTransitions.TryGetValue(instanceId, out var sfx) && !sfx.Settlement.IsCompleted)
            {
                return true;
            }
            if (instanceId == "voice" && voiceTransition is not null && !voiceTransition.Settlement.IsCompleted)
            {
                return true;
            }
            return false;
        }
    }

    public void TrackTransition(AudioCategory category, string key, AudioTransition transition)
    {
        lock (gate)
        {
            switch (category)
            {
                case AudioCategory.Bgm:
                    bgmTransitions[key] = transition;
                    break;
                case AudioCategory.Sfx:
                    sfxTransitions[key] = transition;
                    break;
                case AudioCategory.Voice:
                    voiceTransition = transition;
                    break;
            }
        }

        transition.Settled += () =>
        {
            lock (gate)
            {
                switch (category)
                {
                    case AudioCategory.Bgm:
                        if (bgmTransitions.GetValueOrDefault(key) == transition)
                        {
                            bgmTransitions.Remove(key);
                        }
                        break;
                    case AudioCategory.Sfx:
                        if (sfxTransitions.GetValueOrDefault(key) == transition)
                        {
                            sfxTransitions.Remove(key);
                        }
                        break;
                    case AudioCategory.Voice:
                        if (voiceTransition == transition)
                        {
                            voiceTransition = null;
                        }
                        break;
                }
            }
        };

        _ = ClearTransitionAsync(category, key, transition);
    }

    public AudioTransition? GetActiveTransition(AudioCategory category, string key)
    {
        lock (gate)
        {
            return category switch
            {
                AudioCategory.Bgm => bgmTransitions.GetValueOrDefault(key),
                AudioCategory.Sfx => sfxTransitions.GetValueOrDefault(key),
                AudioCategory.Voice => voiceTransition,
                _ => null,
            };
        }
    }

    public AudioTransition?[] GetTransitions(AudioCategory category, IEnumerable<string> keys)
    {
        lock (gate)
        {
            var dict = category switch
            {
                AudioCategory.Bgm => bgmTransitions,
                AudioCategory.Sfx => sfxTransitions,
                _ => null,
            };

            if (dict is null)
            {
                return category == AudioCategory.Voice && voiceTransition is not null
                    ? [voiceTransition]
                    : Array.Empty<AudioTransition?>();
            }

            return keys.Select(key => dict.GetValueOrDefault(key)).ToArray();
        }
    }

    public AudioTransition? ClearOnNaturalEnd(AudioCategory category, string key)
    {
        lock (gate)
        {
            switch (category)
            {
                case AudioCategory.Bgm:
                    bgmTransitions.TryGetValue(key, out var bgmTrans);
                    return bgmTrans;
                case AudioCategory.Sfx:
                    sfxTransitions.TryGetValue(key, out var sfxTrans);
                    return sfxTrans;
                case AudioCategory.Voice:
                    return voiceTransition;
                default:
                    return null;
            }
        }
    }

    public AudioTransition[] FinishCategoryTransitions(AudioCategory category)
    {
        AudioTransition[] transitions;
        lock (gate)
        {
            transitions = category switch
            {
                AudioCategory.Bgm => bgmTransitions.Values.Distinct().ToArray(),
                AudioCategory.Sfx => sfxTransitions.Values.Distinct().ToArray(),
                AudioCategory.Voice => voiceTransition is null ? [] : [voiceTransition],
                _ => [],
            };
        }

        foreach (var transition in transitions)
        {
            transition.Finish();
        }

        return transitions;
    }

    public Task CompleteTransitionAsync(AudioCategory category, string key, CancellationToken cancellationToken)
    {
        AudioTransition? transition;
        lock (gate)
        {
            transition = category switch
            {
                AudioCategory.Bgm => bgmTransitions.GetValueOrDefault(key),
                AudioCategory.Sfx => sfxTransitions.GetValueOrDefault(key),
                AudioCategory.Voice => voiceTransition,
                _ => null,
            };
        }

        return CompleteTransitionAsync(transition, cancellationToken);
    }

    public Task CompleteTransitionsAsync(
        AudioCategory category,
        IEnumerable<string> keys,
        CancellationToken cancellationToken) =>
        CompleteTransitionsAsync(GetTransitions(category, keys), cancellationToken);

    public static async Task CompleteTransitionsAsync(
        IEnumerable<AudioTransition?> transitions,
        CancellationToken cancellationToken)
    {
        foreach (var transition in transitions.Where(value => value is not null)
                     .Cast<AudioTransition>().Distinct())
        {
            await CompleteTransitionAsync(transition, cancellationToken);
        }
    }

    public async Task CompleteAllTransitionsAsync(CancellationToken cancellationToken)
    {
        AudioTransition?[] snapshot;
        lock (gate)
        {
            snapshot = bgmTransitions.Values
                .Concat(sfxTransitions.Values)
                .Append(voiceTransition)
                .ToArray();
        }

        await CompleteTransitionsAsync(snapshot, cancellationToken);
    }

    public async Task FinishTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        AudioTransition[] snapshot;
        lock (gate)
        {
            snapshot = SelectTransitions(targetId);
        }

        foreach (var transition in snapshot)
        {
            await transition.FinishAsync(cancellationToken);
        }
    }

    public async Task CancelTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        AudioTransition[] snapshot;
        lock (gate)
        {
            snapshot = SelectTransitions(targetId);
        }

        foreach (var transition in snapshot)
        {
            await transition.CancelAsync(cancellationToken);
        }
    }

    public AudioTransition[] SelectTransitions(string? targetId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return bgmTransitions.Values
                    .Concat(sfxTransitions.Values)
                    .Append(voiceTransition)
                    .Where(t => t is not null && !t.Settlement.IsCompleted)
                    .Cast<AudioTransition>()
                    .Distinct()
                    .ToArray();
            }

            var trimmed = targetId.Trim();
            var list = new List<AudioTransition>();
            if (bgmTransitions.TryGetValue(trimmed, out var bgmTransition) && !bgmTransition.Settlement.IsCompleted)
            {
                list.Add(bgmTransition);
            }
            if (sfxTransitions.TryGetValue(trimmed, out var sfxTransition) && !sfxTransition.Settlement.IsCompleted)
            {
                list.Add(sfxTransition);
            }
            if (trimmed == "voice" && voiceTransition is not null && !voiceTransition.Settlement.IsCompleted)
            {
                list.Add(voiceTransition);
            }
            return list.ToArray();
        }
    }

    public void ClearAll()
    {
        lock (gate)
        {
            bgmTransitions.Clear();
            sfxTransitions.Clear();
            voiceTransition = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var transition in bgmTransitions.Values)
            {
                transition.Dispose();
            }
            bgmTransitions.Clear();

            foreach (var transition in sfxTransitions.Values)
            {
                transition.Dispose();
            }
            sfxTransitions.Clear();

            voiceTransition?.Dispose();
            voiceTransition = null;
        }
    }

    private static async Task CompleteTransitionAsync(
        AudioTransition? transition,
        CancellationToken cancellationToken)
    {
        if (transition is null)
        {
            return;
        }

        await transition.FinishAsync(cancellationToken);
    }

    private async Task ClearTransitionAsync(
        AudioCategory category,
        string key,
        AudioTransition transition)
    {
        try { await transition.Settlement; } catch { }
        lock (gate)
        {
            switch (category)
            {
                case AudioCategory.Bgm:
                    if (bgmTransitions.GetValueOrDefault(key) == transition)
                    {
                        bgmTransitions.Remove(key);
                    }
                    break;
                case AudioCategory.Sfx:
                    if (sfxTransitions.GetValueOrDefault(key) == transition)
                    {
                        sfxTransitions.Remove(key);
                    }
                    break;
                case AudioCategory.Voice:
                    if (voiceTransition == transition)
                    {
                        voiceTransition = null;
                    }
                    break;
            }
        }
    }
}

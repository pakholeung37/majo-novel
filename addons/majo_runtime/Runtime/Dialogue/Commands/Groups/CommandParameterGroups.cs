#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record TransitionArguments(
    CommandValue<double> Duration,
    CommandValue<EasingId> Ease,
    CommandValue<DirectionTransitionId> Via,
    CommandValue<bool> Wait)
{
    public TimingArguments Timing => new(Duration, Ease, Wait);
}

public readonly record struct TransitionParameterOptions(
    bool WaitContractDefault = false,
    int ViaOrder = 20,
    int DurationOrder = 21,
    int EaseOrder = 22,
    int WaitOrder = 23);

public sealed record TimingArguments(
    CommandValue<double> Duration,
    CommandValue<EasingId> Ease,
    CommandValue<bool> Wait)
{
    public TransitionArguments ToTransitionArguments(DirectionTransitionId via = default) =>
        new(
            Duration,
            Ease,
            via.IsEmpty ? CommandValue<DirectionTransitionId>.Unspecified() : CommandValue<DirectionTransitionId>.Explicit(via),
            Wait);
}

public readonly record struct TimingParameterOptions(
    bool WaitContractDefault = false,
    int DurationOrder = 21,
    int EaseOrder = 22,
    int WaitOrder = 23);

public sealed record WaitArguments(
    CommandValue<bool> Wait);

public sealed record AudioFadeArguments(
    CommandValue<double> Duration);

public static class CommandParameterGroups
{
    public static ICommandParameterGroup<TransitionArguments> Transition(
        TransitionParameterOptions options = default)
    {
        var parameters = new[]
        {
            new CommandParameterSpec(
                "via",
                options.ViaOrder,
                Primary: false,
                Required: false,
                typeof(DirectionTransitionId),
                "kind"),
            new CommandParameterSpec(
                "duration",
                options.DurationOrder,
                Primary: false,
                Required: false,
                typeof(double),
                "seconds",
                Minimum: 0),
            new CommandParameterSpec(
                "ease",
                options.EaseOrder,
                Primary: false,
                Required: false,
                typeof(EasingId),
                "curve"),
            new CommandParameterSpec(
                "wait",
                options.WaitOrder,
                Primary: false,
                Required: false,
                typeof(bool),
                "bool",
                ContractDefault: options.WaitContractDefault),
        };

        return new DelegateCommandParameterGroup<TransitionArguments>(
            "transition",
            parameters,
            (raw, env) =>
            {
                var duration = BindValue<double>(raw, env, "duration");
                var ease = BindValue<EasingId>(raw, env, "ease");
                var via = BindValue<DirectionTransitionId>(raw, env, "via");
                var wait = BindValue(raw, env, "wait", options.WaitContractDefault);

                return new TransitionArguments(duration, ease, via, wait);
            });
    }

    public static ICommandParameterGroup<TimingArguments> Timing(
        TimingParameterOptions options = default)
    {
        var parameters = new[]
        {
            new CommandParameterSpec(
                "duration",
                options.DurationOrder,
                Primary: false,
                Required: false,
                typeof(double),
                "seconds",
                Minimum: 0),
            new CommandParameterSpec(
                "ease",
                options.EaseOrder,
                Primary: false,
                Required: false,
                typeof(EasingId),
                "curve"),
            new CommandParameterSpec(
                "wait",
                options.WaitOrder,
                Primary: false,
                Required: false,
                typeof(bool),
                "bool",
                ContractDefault: options.WaitContractDefault),
        };

        return new DelegateCommandParameterGroup<TimingArguments>(
            "timing",
            parameters,
            (raw, env) =>
            {
                var duration = BindValue<double>(raw, env, "duration");
                var ease = BindValue<EasingId>(raw, env, "ease");
                var wait = BindValue(raw, env, "wait", options.WaitContractDefault);

                return new TimingArguments(duration, ease, wait);
            });
    }

    public static ICommandParameterGroup<WaitArguments> Wait(
        bool contractDefault = false,
        int order = 20)
    {
        var parameters = new[]
        {
            new CommandParameterSpec(
                "wait",
                order,
                Primary: false,
                Required: false,
                typeof(bool),
                "bool",
                ContractDefault: contractDefault),
        };

        return new DelegateCommandParameterGroup<WaitArguments>(
            "wait",
            parameters,
            (raw, env) =>
            {
                var wait = BindValue(raw, env, "wait", contractDefault);
                return new WaitArguments(wait);
            });
    }

    public static ICommandParameterGroup<AudioFadeArguments> AudioFade(
        int order = 10)
    {
        var parameters = new[]
        {
            new CommandParameterSpec(
                "duration",
                order,
                Primary: false,
                Required: false,
                typeof(double),
                "seconds",
                Minimum: 0),
        };

        return new DelegateCommandParameterGroup<AudioFadeArguments>(
            "audioFade",
            parameters,
            (raw, env) =>
            {
                var duration = BindValue<double>(raw, env, "duration");
                return new AudioFadeArguments(duration);
            });
    }

    private static CommandValue<T> BindValue<T>(
        RawCommandArguments raw,
        CommandBindingEnvironment env,
        string name,
        T? contractDefault = default)
    {
        if (raw.Named.TryGetValue(name, out var rawText))
        {
            var converted = env.Converters.Convert<T>(rawText);
            return CommandValue<T>.Explicit(converted);
        }

        return contractDefault is not null
            ? CommandValue<T>.ContractDefault(contractDefault)
            : CommandValue<T>.Unspecified();
    }
}

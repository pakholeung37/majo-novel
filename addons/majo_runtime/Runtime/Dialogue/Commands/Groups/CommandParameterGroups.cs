#nullable enable

using System;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class BlockingWait : ICommandGroupConfiguration
{
    public void Configure(CommandGroupConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetDefaultText("Wait", "true");
    }
}

public sealed class NonBlockingWait : ICommandGroupConfiguration
{
    public void Configure(CommandGroupConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetDefaultText("Wait", "false");
    }
}

public sealed record TransitionArguments(
    [Option(ValueHint = "seconds", Minimum = 0, Order = 21)]
    CommandValue<double> Duration,
    [Option(ValueHint = "curve", Order = 22)]
    CommandValue<EasingId> Ease,
    [Option(ValueHint = "kind", Order = 20)]
    CommandValue<DirectionTransitionId> Via,
    [Option(ValueHint = "bool", DefaultText = "false", Order = 23)]
    CommandValue<bool> Wait)
{
    public TimingArguments Timing => new(Duration, Ease, Wait);

    public static TransitionArguments Create(
        CommandValue<DirectionTransitionId>? via = null,
        CommandValue<double>? duration = null,
        CommandValue<EasingId>? ease = null,
        CommandValue<bool>? wait = null) =>
        new(
            duration ?? CommandValue<double>.Unspecified(),
            ease ?? CommandValue<EasingId>.Unspecified(),
            via ?? CommandValue<DirectionTransitionId>.Unspecified(),
            wait ?? CommandValue<bool>.Unspecified());
}

public readonly record struct TransitionParameterOptions(
    bool WaitContractDefault = false,
    int ViaOrder = 20,
    int DurationOrder = 21,
    int EaseOrder = 22,
    int WaitOrder = 23);

public sealed record TimingArguments(
    [Option(ValueHint = "seconds", Minimum = 0, Order = 21)]
    CommandValue<double> Duration,
    [Option(ValueHint = "curve", Order = 22)]
    CommandValue<EasingId> Ease,
    [Option(ValueHint = "bool", DefaultText = "false", Order = 23)]
    CommandValue<bool> Wait)
{
    public TransitionArguments ToTransitionArguments(DirectionTransitionId via = default) =>
        new(
            Duration,
            Ease,
            via.IsEmpty ? CommandValue<DirectionTransitionId>.Unspecified() : CommandValue<DirectionTransitionId>.Explicit(via),
            Wait);

    public static TimingArguments Create(
        CommandValue<double>? duration = null,
        CommandValue<EasingId>? ease = null,
        CommandValue<bool>? wait = null) =>
        new(
            duration ?? CommandValue<double>.Unspecified(),
            ease ?? CommandValue<EasingId>.Unspecified(),
            wait ?? CommandValue<bool>.Unspecified());
}

public readonly record struct TimingParameterOptions(
    bool WaitContractDefault = false,
    int DurationOrder = 21,
    int EaseOrder = 22,
    int WaitOrder = 23);

public sealed record WaitArguments(
    [Option(ValueHint = "bool", DefaultText = "false", Order = 20)]
    CommandValue<bool> Wait);

public sealed record AudioFadeArguments(
    [Option(ValueHint = "seconds", Minimum = 0, Order = 10)]
    CommandValue<double> Duration);

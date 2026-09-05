#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

public sealed class PlayBgmCommand : RuntimeCommand<PlayBgmCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlayBgmCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "bgm",
                category: "Audio",
                usage: "bgm <clip> [track:<id>] [loop:<bool>] [volume:<0..1>] [duration:<seconds>] [wait:<bool>]")
            .Primary<string>("clip")
            .Optional<string>("track")
            .Optional<bool>("loop", contractDefault: true, hasContractDefault: true)
            .Optional<double>("volume", contractDefault: 1.0, hasContractDefault: true, minimum: 0)
            .Use("fade", CommandParameterGroups.AudioFade())
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Required<string>("clip"),
                values.Optional<string>("track").GetValueOrDefault(string.Empty),
                values.Optional<bool>("loop").GetValueOrDefault(true),
                values.Optional<double>("volume").GetValueOrDefault(1.0),
                values.Group<AudioFadeArguments>("fade"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Bgm, context);
        var track = string.IsNullOrWhiteSpace(arguments.Track) ? null : arguments.Track;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var result = await audio.PlayBgmAsync(
            arguments.Clip,
            track,
            arguments.Loop,
            (float)arguments.Volume,
            AudioCommandExecution.Fade(fadeDuration),
            context.CancellationToken);

        await AudioCommandExecution.WaitSettlementAsync(
            result.Settlement, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        string Clip,
        string Track,
        bool Loop,
        double Volume,
        AudioFadeArguments Fade,
        WaitArguments Wait);
}

public sealed class StopBgmCommand : RuntimeCommand<StopBgmCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public StopBgmCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "stopBgm",
                category: "Audio",
                usage: "stopBgm [track:<id>] [duration:<seconds>] [wait:<bool>]")
            .Optional<string>("track")
            .Use("fade", CommandParameterGroups.AudioFade())
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Optional<string>("track").GetValueOrDefault(string.Empty),
                values.Group<AudioFadeArguments>("fade"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Bgm, context);
        var track = string.IsNullOrWhiteSpace(arguments.Track) ? null : arguments.Track;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var stopTask = audio.StopBgmAsync(
            track,
            AudioCommandExecution.Fade(fadeDuration),
            wait ? context.CancellationToken : CancellationToken.None);

        await AudioCommandExecution.WaitSettlementAsync(stopTask, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        string Track,
        AudioFadeArguments Fade,
        WaitArguments Wait);
}

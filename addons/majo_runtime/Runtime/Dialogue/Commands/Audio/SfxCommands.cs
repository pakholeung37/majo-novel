#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

public sealed class PlaySfxCommand : RuntimeCommand<PlaySfxCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlaySfxCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "sfx",
                category: "Audio",
                usage: "sfx <clip> [instance:<id>] [loop:<bool>] [volume:<0..1>] [duration:<seconds>] [wait:<bool>] [waitPlayback:<bool>]")
            .Primary<string>("clip")
            .Optional<string>("instance")
            .Optional<bool>("loop")
            .Optional<double>("volume", contractDefault: 1.0, hasContractDefault: true, minimum: 0)
            .Optional<bool>("waitPlayback")
            .Use("fade", CommandParameterGroups.AudioFade())
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Required<string>("clip"),
                values.Optional<string>("instance").GetValueOrDefault(string.Empty),
                values.Optional<bool>("loop").GetValueOrDefault(false),
                values.Optional<double>("volume").GetValueOrDefault(1.0),
                values.Optional<bool>("waitPlayback").GetValueOrDefault(false),
                values.Group<AudioFadeArguments>("fade"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Sfx, context);
        var instance = string.IsNullOrWhiteSpace(arguments.Instance) ? null : arguments.Instance;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var result = await audio.PlaySfxAsync(
            arguments.Clip,
            instance,
            arguments.Loop,
            (float)arguments.Volume,
            AudioCommandExecution.Fade(fadeDuration),
            context.CancellationToken);

        await AudioCommandExecution.WaitSettlementAsync(
            result.Settlement, wait, context.CancellationToken);

        if (arguments.WaitPlayback && !arguments.Loop)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }

    public sealed record Arguments(
        string Clip,
        string Instance,
        bool Loop,
        double Volume,
        bool WaitPlayback,
        AudioFadeArguments Fade,
        WaitArguments Wait);
}

public sealed class StopSfxCommand : RuntimeCommand<StopSfxCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public StopSfxCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "stopSfx",
                category: "Audio",
                usage: "stopSfx [instance:<id>] [duration:<seconds>] [wait:<bool>]")
            .Optional<string>("instance")
            .Use("fade", CommandParameterGroups.AudioFade())
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Optional<string>("instance").GetValueOrDefault(string.Empty),
                values.Group<AudioFadeArguments>("fade"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Sfx, context);
        var instance = string.IsNullOrWhiteSpace(arguments.Instance) ? null : arguments.Instance;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var stopTask = audio.StopSfxAsync(
            instance,
            AudioCommandExecution.Fade(fadeDuration),
            wait ? context.CancellationToken : CancellationToken.None);

        await AudioCommandExecution.WaitSettlementAsync(stopTask, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        string Instance,
        AudioFadeArguments Fade,
        WaitArguments Wait);
}

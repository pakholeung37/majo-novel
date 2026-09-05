#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

public sealed class PlayVoiceCommand : RuntimeCommand<PlayVoiceCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlayVoiceCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "voice",
                category: "Audio",
                usage: "voice <clip> [volume:<0..1>] [duration:<seconds>] [wait:<bool>]")
            .Primary<string>("clip")
            .Optional<double>("volume", contractDefault: 1.0, hasContractDefault: true, minimum: 0)
            .Use("fade", CommandParameterGroups.AudioFade())
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Required<string>("clip"),
                values.Optional<double>("volume").GetValueOrDefault(1.0),
                values.Group<AudioFadeArguments>("fade"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Voice, context);
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var result = await audio.PlayVoiceAsync(
            arguments.Clip,
            volume: (float)arguments.Volume,
            fade: AudioCommandExecution.Fade(fadeDuration),
            cancellationToken: context.CancellationToken);

        if (wait)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }

    public sealed record Arguments(
        string Clip,
        double Volume,
        AudioFadeArguments Fade,
        WaitArguments Wait);
}

public sealed class StopVoiceCommand : RuntimeCommand<StopVoiceCommand.Arguments>
{
    private readonly IAudioManager audio;

    public StopVoiceCommand(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("stopVoice", category: "Audio", usage: "stopVoice")
            .Build(static _ => new Arguments());

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await audio.StopVoiceAsync(cancellationToken: context.CancellationToken);
    }

    public sealed record Arguments;
}

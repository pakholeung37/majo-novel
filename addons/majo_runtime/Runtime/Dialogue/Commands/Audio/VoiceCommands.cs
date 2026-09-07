#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[Command("voice", Category = "Audio")]
public sealed partial class PlayVoiceCommand : RuntimeCommand<PlayVoiceCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlayVoiceCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Voice, context);
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var result = await audio.PlayVoiceAsync(
            arguments.Clip,
            volume: (float)arguments.Volume.GetValueOrDefault(1.0),
            fade: AudioCommandExecution.Fade(fadeDuration),
            cancellationToken: context.CancellationToken);

        if (wait)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }

    public sealed record Arguments(
        [Primary] string Clip,
        [Option(DefaultText = "1.0", Minimum = 0)] CommandValue<double> Volume,
        [Group] AudioFadeArguments Fade,
        [Group] WaitArguments Wait);
}

[Command("stopVoice", Category = "Audio")]
public sealed partial class StopVoiceCommand : RuntimeCommand<StopVoiceCommand.Arguments>
{
    private readonly IAudioManager audio;

    public StopVoiceCommand(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await audio.StopVoiceAsync(cancellationToken: context.CancellationToken);
    }

    public sealed record Arguments;
}

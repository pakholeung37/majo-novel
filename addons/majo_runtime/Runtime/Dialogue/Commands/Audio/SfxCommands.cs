#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[Command("sfx", Category = "Audio")]
public sealed partial class PlaySfxCommand : RuntimeCommand<PlaySfxCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlaySfxCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Sfx, context);
        var instance = arguments.Instance.WasProvided && !string.IsNullOrWhiteSpace(arguments.Instance.Value) ? arguments.Instance.Value : null;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);
        var loop = arguments.Loop.GetValueOrDefault(false);
        var waitPlayback = arguments.WaitPlayback.GetValueOrDefault(false);

        var result = await audio.PlaySfxAsync(
            arguments.Clip,
            instance,
            loop,
            (float)arguments.Volume.GetValueOrDefault(1.0),
            AudioCommandExecution.Fade(fadeDuration),
            context.CancellationToken);

        await AudioCommandExecution.WaitSettlementAsync(
            result.Settlement, wait, context.CancellationToken);

        if (waitPlayback && !loop)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }

    public sealed record Arguments(
        [Primary] string Clip,
        [Option] CommandValue<string> Instance,
        [Option] CommandValue<bool> Loop,
        [Option(DefaultText = "1.0", Minimum = 0)] CommandValue<double> Volume,
        [Option] CommandValue<bool> WaitPlayback,
        [Group] AudioFadeArguments Fade,
        [Group] WaitArguments Wait);
}

[Command("stopSfx", Category = "Audio")]
public sealed partial class StopSfxCommand : RuntimeCommand<StopSfxCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public StopSfxCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Sfx, context);
        var instance = arguments.Instance.WasProvided && !string.IsNullOrWhiteSpace(arguments.Instance.Value) ? arguments.Instance.Value : null;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var stopTask = audio.StopSfxAsync(
            instance,
            AudioCommandExecution.Fade(fadeDuration),
            wait ? context.CancellationToken : CancellationToken.None);

        await AudioCommandExecution.WaitSettlementAsync(stopTask, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        [Option] CommandValue<string> Instance,
        [Group] AudioFadeArguments Fade,
        [Group] WaitArguments Wait);
}

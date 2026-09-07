#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[Command("bgm", Category = "Audio")]
public sealed partial class PlayBgmCommand : RuntimeCommand<PlayBgmCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public PlayBgmCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Bgm, context);
        var track = arguments.Track.WasProvided && !string.IsNullOrWhiteSpace(arguments.Track.Value) ? arguments.Track.Value : null;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var result = await audio.PlayBgmAsync(
            arguments.Clip,
            track,
            arguments.Loop.GetValueOrDefault(true),
            (float)arguments.Volume.GetValueOrDefault(1.0),
            AudioCommandExecution.Fade(fadeDuration),
            context.CancellationToken);

        await AudioCommandExecution.WaitSettlementAsync(
            result.Settlement, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Clip,
        [Option] CommandValue<string> Track,
        [Option(DefaultText = "true")] CommandValue<bool> Loop,
        [Option(DefaultText = "1.0", Minimum = 0)] CommandValue<double> Volume,
        [Group] AudioFadeArguments Fade,
        [Group] WaitArguments Wait);
}

[Command("stopBgm", Category = "Audio")]
public sealed partial class StopBgmCommand : RuntimeCommand<StopBgmCommand.Arguments>
{
    private readonly IAudioManager audio;
    private readonly IAudioCommandPolicy policy;

    public StopBgmCommand(IAudioManager audio, IAudioCommandPolicy policy)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var fadeDuration = policy.ResolveFadeDuration(arguments.Fade, AudioCommandKind.Bgm, context);
        var track = arguments.Track.WasProvided && !string.IsNullOrWhiteSpace(arguments.Track.Value) ? arguments.Track.Value : null;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var stopTask = audio.StopBgmAsync(
            track,
            AudioCommandExecution.Fade(fadeDuration),
            wait ? context.CancellationToken : CancellationToken.None);

        await AudioCommandExecution.WaitSettlementAsync(stopTask, wait, context.CancellationToken);
    }

    public sealed record Arguments(
        [Option] CommandValue<string> Track,
        [Group] AudioFadeArguments Fade,
        [Group] WaitArguments Wait);
}

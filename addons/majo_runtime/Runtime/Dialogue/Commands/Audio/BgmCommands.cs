#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[RuntimeCommand(
    "bgm",
    Category = "Audio",
    Usage = "bgm <clip> [track:<id>] [loop:<bool>] [volume:<0..1>] [duration:<seconds>] [wait:<bool>]")]
public sealed class PlayBgmCommandArguments
{
    [CommandParameter("clip", 0, Primary = true, Required = true)]
    public string Clip { get; set; } = string.Empty;

    [CommandParameter("track", 1)]
    public string? Track { get; set; }

    [CommandParameter("loop", 2, DefaultValue = "true")]
    public bool Loop { get; set; }

    [CommandParameter("volume", 3, DefaultValue = "1", Minimum = 0)]
    public double Volume { get; set; }

    [CommandParameter("duration", 4, DefaultSource = CommandDefaultSource.AudioFadeDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 5)]
    public bool Wait { get; set; }

}

public sealed class PlayBgmCommandHandler : IRuntimeCommandHandler<PlayBgmCommandArguments>
{
    private readonly IAudioManager audio;

    public PlayBgmCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        PlayBgmCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var result = await audio.PlayBgmAsync(
            arguments.Clip,
            arguments.Track,
            arguments.Loop,
            (float)arguments.Volume,
            AudioCommandExecution.Fade(
                arguments.Duration),
            context.CancellationToken);
        await AudioCommandExecution.WaitSettlementAsync(
            result.Settlement, arguments.Wait, context.CancellationToken);
    }
}

[RuntimeCommand(
    "stopBgm",
    Category = "Audio",
    Usage = "stopBgm [track:<id>] [duration:<seconds>] [wait:<bool>]")]
public sealed class StopBgmCommandArguments
{
    [CommandParameter("track", 0)]
    public string? Track { get; set; }

    [CommandParameter("duration", 1, DefaultSource = CommandDefaultSource.AudioFadeDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 2)]
    public bool Wait { get; set; }

}

public sealed class StopBgmCommandHandler : IRuntimeCommandHandler<StopBgmCommandArguments>
{
    private readonly IAudioManager audio;

    public StopBgmCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        StopBgmCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var stopTask = audio.StopBgmAsync(
            arguments.Track,
            AudioCommandExecution.Fade(
                arguments.Duration),
            arguments.Wait ? context.CancellationToken : System.Threading.CancellationToken.None);
        await AudioCommandExecution.WaitSettlementAsync(stopTask, arguments.Wait, context.CancellationToken);
    }
}

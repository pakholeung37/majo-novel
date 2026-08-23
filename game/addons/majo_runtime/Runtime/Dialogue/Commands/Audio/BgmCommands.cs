#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[RuntimeCommand(
    "bgm",
    Category = "Audio",
    Usage = "bgm <clip> [track:<id>] [loop:<bool>] [volume:<0..1>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
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

    [CommandParameter("group", 6)]
    public string? Group { get; set; }

    [CommandParameter("operation", 7)]
    public string? Operation { get; set; }
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
                arguments.Duration, arguments.Operation, arguments.Group),
            context.CancellationToken);
        await AudioCommandExecution.TrackAndWaitAsync(
            result.FadeOperation, arguments.Wait, context);
    }
}

[RuntimeCommand(
    "stopBgm",
    Category = "Audio",
    Usage = "stopBgm [track:<id>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StopBgmCommandArguments
{
    [CommandParameter("track", 0)]
    public string? Track { get; set; }

    [CommandParameter("duration", 1, DefaultSource = CommandDefaultSource.AudioFadeDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 2)]
    public bool Wait { get; set; }

    [CommandParameter("group", 3)]
    public string? Group { get; set; }

    [CommandParameter("operation", 4)]
    public string? Operation { get; set; }
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
        var operation = await audio.StopBgmAsync(
            arguments.Track,
            AudioCommandExecution.Fade(
                arguments.Duration, arguments.Operation, arguments.Group),
            context.CancellationToken);
        await AudioCommandExecution.TrackAndWaitAsync(operation, arguments.Wait, context);
    }
}

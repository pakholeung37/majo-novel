#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[RuntimeCommand(
    "sfx",
    Category = "Audio",
    Usage = "sfx <clip> [instance:<id>] [loop:<bool>] [volume:<0..1>] [duration:<seconds>] [wait:<bool>] [waitPlayback:<bool>] [group:<id>] [operation:<id>]")]
public sealed class PlaySfxCommandArguments
{
    [CommandParameter("clip", 0, Primary = true, Required = true)]
    public string Clip { get; set; } = string.Empty;

    [CommandParameter("instance", 1)]
    public string? Instance { get; set; }

    [CommandParameter("loop", 2)]
    public bool Loop { get; set; }

    [CommandParameter("volume", 3, DefaultValue = "1", Minimum = 0)]
    public double Volume { get; set; }

    [CommandParameter("duration", 4, DefaultSource = CommandDefaultSource.AudioFadeDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 5)]
    public bool Wait { get; set; }

    [CommandParameter("waitPlayback", 6)]
    public bool WaitPlayback { get; set; }

    [CommandParameter("group", 7)]
    public string? Group { get; set; }

    [CommandParameter("operation", 8)]
    public string? Operation { get; set; }
}

public sealed class PlaySfxCommandHandler : IRuntimeCommandHandler<PlaySfxCommandArguments>
{
    private readonly IAudioManager audio;

    public PlaySfxCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        PlaySfxCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var result = await audio.PlaySfxAsync(
            arguments.Clip,
            arguments.Instance,
            arguments.Loop,
            (float)arguments.Volume,
            AudioCommandExecution.Fade(
                arguments.Duration, arguments.Operation, arguments.Group),
            context.CancellationToken);
        await AudioCommandExecution.TrackAndWaitAsync(
            result.FadeOperation, arguments.Wait, context);
        if (arguments.WaitPlayback && !arguments.Loop)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }
}

[RuntimeCommand(
    "stopSfx",
    Category = "Audio",
    Usage = "stopSfx [instance:<id>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StopSfxCommandArguments
{
    [CommandParameter("instance", 0)]
    public string? Instance { get; set; }

    [CommandParameter("duration", 1, DefaultSource = CommandDefaultSource.AudioFadeDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 2)]
    public bool Wait { get; set; }

    [CommandParameter("group", 3)]
    public string? Group { get; set; }

    [CommandParameter("operation", 4)]
    public string? Operation { get; set; }
}

public sealed class StopSfxCommandHandler : IRuntimeCommandHandler<StopSfxCommandArguments>
{
    private readonly IAudioManager audio;

    public StopSfxCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        StopSfxCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var operation = await audio.StopSfxAsync(
            arguments.Instance,
            AudioCommandExecution.Fade(
                arguments.Duration, arguments.Operation, arguments.Group),
            context.CancellationToken);
        await AudioCommandExecution.TrackAndWaitAsync(operation, arguments.Wait, context);
    }
}

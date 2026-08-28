#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

[RuntimeCommand(
    "voice",
    Category = "Audio",
    Usage = "voice <clip> [volume:<0..1>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class PlayVoiceCommandArguments
{
    [CommandParameter("clip", 0, Primary = true, Required = true)]
    public string Clip { get; set; } = string.Empty;

    [CommandParameter("volume", 1, DefaultValue = "1", Minimum = 0)]
    public double Volume { get; set; }

    [CommandParameter("duration", 2, DefaultValue = RuntimeDefaults.ImmediateDurationSecondsText, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 3)]
    public bool Wait { get; set; }

    [CommandParameter("group", 4)]
    public string? Group { get; set; }

    [CommandParameter("operation", 5)]
    public string? Operation { get; set; }
}

public sealed class PlayVoiceCommandHandler : IRuntimeCommandHandler<PlayVoiceCommandArguments>
{
    private readonly IAudioManager audio;

    public PlayVoiceCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        PlayVoiceCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var result = await audio.PlayVoiceAsync(
            arguments.Clip,
            volume: (float)arguments.Volume,
            fade: AudioCommandExecution.Fade(
                arguments.Duration, arguments.Operation, arguments.Group),
            cancellationToken: context.CancellationToken);
        context.Track(result.FadeOperation);
        if (arguments.Wait)
        {
            await result.Playback.Completion.WaitAsync(context.CancellationToken);
        }
    }
}

[RuntimeCommand("stopVoice", Category = "Audio", Usage = "stopVoice")]
public sealed class StopVoiceCommandArguments
{
}

public sealed class StopVoiceCommandHandler : IRuntimeCommandHandler<StopVoiceCommandArguments>
{
    private readonly IAudioManager audio;

    public StopVoiceCommandHandler(IAudioManager audio) =>
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public async Task ExecuteAsync(
        StopVoiceCommandArguments arguments,
        RuntimeCommandContext context)
    {
        _ = arguments;
        var operation = await audio.StopVoiceAsync(
            cancellationToken: context.CancellationToken);
        if (operation is not null)
        {
            await AudioCommandExecution.TrackAndWaitAsync(operation, true, context);
        }
    }
}

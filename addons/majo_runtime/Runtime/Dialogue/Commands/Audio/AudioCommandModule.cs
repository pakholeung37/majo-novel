#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands.Audio;

public sealed class AudioCommandModule : IRuntimeCommandModule
{
    private readonly IAudioManager audio;

    public AudioCommandModule(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new PlayBgmCommandHandler(audio))
            .Register(new StopBgmCommandHandler(audio))
            .Register(new PlaySfxCommandHandler(audio))
            .Register(new StopSfxCommandHandler(audio))
            .Register(new PlayVoiceCommandHandler(audio))
            .Register(new StopVoiceCommandHandler(audio));
    }
}

internal static class AudioCommandExecution
{
    public static AudioFade Fade(
        double duration,
        string? operation,
        string? group) => new(duration, operation, group);

    public static async Task TrackAndWaitAsync(
        IOperationHandle operation,
        bool wait,
        RuntimeCommandContext context)
    {
        context.Track(operation);
        if (wait)
        {
            await operation.Completion.WaitAsync(context.CancellationToken);
        }
    }
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

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
    public static AudioFade Fade(double duration) => new(duration);

    public static async Task WaitSettlementAsync(
        Task settlement,
        bool wait,
        CancellationToken cancellationToken)
    {
        if (wait)
        {
            await settlement.WaitAsync(cancellationToken);
        }
    }
}


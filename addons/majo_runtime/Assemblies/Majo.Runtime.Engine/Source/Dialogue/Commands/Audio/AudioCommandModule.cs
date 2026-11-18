#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;

namespace Majo.Runtime.Dialogue.Commands.Audio;

public sealed class AudioCommandModule : IRuntimeCommandModule
{
    public void Register(CommandRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RegisterCommands(context.Commands);
    }

    public static void RegisterCommands(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        const string providerId = "majo.audio";
        const string version = "1.0";

        commands.Add("bgm", services => new PlayBgmCommand(services.Get<IAudioManager>(), services.Get<IAudioCommandPolicy>()), providerId, version);
        commands.Add("stopBgm", services => new StopBgmCommand(services.Get<IAudioManager>(), services.Get<IAudioCommandPolicy>()), providerId, version);
        commands.Add("sfx", services => new PlaySfxCommand(services.Get<IAudioManager>(), services.Get<IAudioCommandPolicy>()), providerId, version);
        commands.Add("stopSfx", services => new StopSfxCommand(services.Get<IAudioManager>(), services.Get<IAudioCommandPolicy>()), providerId, version);
        commands.Add("voice", services => new PlayVoiceCommand(services.Get<IAudioManager>(), services.Get<IAudioCommandPolicy>()), providerId, version);
        commands.Add("stopVoice", services => new StopVoiceCommand(services.Get<IAudioManager>()), providerId, version);
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

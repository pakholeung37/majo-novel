#nullable enable

using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterAudioServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<IAudioCommandPolicy>(_ =>
                new AudioCommandPolicy(AudioFadeDefaults.FromConfiguration(Configuration?.Animation)))
            .AddSingleton<IAudioManager>(
                resolver => CreateAudioManager(
                    resolver.Get<IResourceManager>(),
                    resolver.Get<IMissingResourceReporter>(),
                    resolver.Get<ILocaleManager>(),
                    resolver.Get<ILocalizedResourceResolver>(),
                    resolver.Get<IRuntimeActivityRegistry>()),
                typeof(IResourceManager),
                typeof(IMissingResourceReporter),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver),
                typeof(IRuntimeActivityRegistry));
    }

    private AudioManager CreateAudioManager(
        IResourceManager resources,
        IMissingResourceReporter missingResources,
        ILocaleManager locales,
        ILocalizedResourceResolver localizedResources,
        IRuntimeActivityRegistry activities)
    {
        var configuration = Configuration!.Audio!;
        return new AudioManager(
            AudioBackendFactory?.Invoke(this) ?? new GodotAudioBackend(this),
            new GodotAudioClipLoader(resources, configuration.BgmPath),
            new GodotAudioClipLoader(resources, configuration.SfxPath),
            new GodotAudioClipLoader(resources, configuration.VoicePath),
            new AudioSettings(
                configuration.MasterVolume,
                configuration.BgmVolume,
                configuration.SfxVolume,
                configuration.VoiceVolume,
                configuration.MasterMuted,
                configuration.BgmMuted,
                configuration.SfxMuted,
                configuration.VoiceMuted,
                configuration.VoiceLocale,
                configuration.FailOnMissingClips),
            Logger,
            missingResources,
            locales,
            localizedResources,
            activities);
    }
}

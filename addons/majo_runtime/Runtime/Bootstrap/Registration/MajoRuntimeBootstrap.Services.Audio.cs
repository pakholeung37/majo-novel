#nullable enable

using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterAudioServices(RuntimeServiceRegistry services)
    {
        services.AddSingleton<IAudioManager>(
            resolver => CreateAudioManager(
                resolver.Get<IResourceManager>(),
                resolver.Get<IOperationRunner>(),
                resolver.Get<IMissingResourceReporter>(),
                resolver.Get<ILocaleManager>(),
                resolver.Get<ILocalizedResourceResolver>(),
                resolver.Get<IRuntimeActivityRegistry>()),
            typeof(IResourceManager),
            typeof(IOperationRunner),
            typeof(IMissingResourceReporter),
            typeof(ILocaleManager),
            typeof(ILocalizedResourceResolver),
            typeof(IRuntimeActivityRegistry));
    }

    private AudioManager CreateAudioManager(
        IResourceManager resources,
        IOperationRunner operations,
        IMissingResourceReporter missingResources,
        ILocaleManager locales,
        ILocalizedResourceResolver localizedResources,
        IRuntimeActivityRegistry activities)
    {
        var configuration = Configuration!.Audio!;
        return new AudioManager(
            AudioBackendFactory?.Invoke(this) ?? new GodotAudioBackend(this),
            operations,
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

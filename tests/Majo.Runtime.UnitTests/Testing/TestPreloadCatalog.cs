using Majo.Runtime.Audio;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;

namespace Majo.Runtime.UnitTests.Testing;

internal static class TestPreloadCatalog
{
    public static RuntimeCommandCatalog Create(params IRuntimeCommand[] additional)
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var nodes = new DelegateDialogueNodeAccessor(() => "Start");
        var policy = new DirectionCommandPolicy(beats);
        var clips = new FakeAudioClipLoader("audio");
        var audio = new AudioManager(new FakeAudioBackend(), clips, clips, clips, new AudioSettings());
        var fades = new AudioCommandPolicy(AudioFadeDefaults.FromConfiguration(null));
        var builder = new TestCatalogBuilder()
            .Add(new CharacterCommand(beats, scopes, nodes, policy))
            .Add(new CharacterMoveCommand(beats, scopes, nodes, policy))
            .Add(new CharacterHideCommand(beats, scopes, nodes, policy))
            .Add(new CharacterFxCommand(beats, scopes, nodes, policy))
            .Add(new ObjectCommand(beats, scopes, nodes, policy))
            .Add(new ObjectMoveCommand(beats, scopes, nodes, policy))
            .Add(new ObjectHideCommand(beats, scopes, nodes, policy))
            .Add(new ObjectFxCommand(beats, scopes, nodes, policy))
            .Add(new BackdropCommand(beats, scopes, nodes, policy))
            .Add(new BackdropFxCommand(beats, scopes, nodes, policy))
            .Add(new BeatBeginCommand(beats, nodes))
            .Add(new BeatEndCommand(beats))
            .Add(new PlayBgmCommand(audio, fades))
            .Add(new PlaySfxCommand(audio, fades))
            .Add(new PlayVoiceCommand(audio, fades));
        foreach (var command in additional) builder.Add(command);
        return builder.Build();
    }
}

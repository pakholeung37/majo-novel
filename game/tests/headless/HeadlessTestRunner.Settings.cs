using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Input;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.Settings;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateSettingsRuntimeAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.State!.StorageNamespace = $"majo-headless-m14-{Guid.NewGuid():N}";
        var first = new MajoRuntimeBootstrap
        {
            Name = "SettingsRuntimeFirst",
            Configuration = configuration,
        };
        AddChild(first);
        await first.InitializationCompletion;
        var settings = first.Host!.Services.Get<ISettingsManager>();
        var audio = first.Host.Services.Get<IAudioManager>();
        var playback = first.Host.Services.Get<IPlaybackModeManager>();
        var input = first.Host.Services.Get<IInputManager>();
        var overlay = first.Host.Services.Get<ISettingsOverlay>();
        var baselineHandles = input.ActiveHandleCount;
        settings.Update(settings.Current with
        {
            MasterVolume = 0.42f,
            LettersPerSecond = 73,
            AutoExtraDelaySeconds = 1.75,
            AutoWaitForVoice = false,
        });
        Assert(audio.MasterVolume == 0.42f &&
                playback.ExtraDelay == TimeSpan.FromSeconds(1.75) &&
                !playback.WaitForVoice,
            "M14 settings did not immediately project Audio and Auto values.");
        overlay.Open();
        Assert(overlay.IsOpen && input.ActiveHandleCount == baselineHandles + 1,
            "M14 Settings UI did not acquire exactly one modal input scope.");
        overlay.Close();
        Assert(!overlay.IsOpen && input.ActiveHandleCount == baselineHandles,
            "M14 Settings UI did not release its modal input scope.");
        await settings.FlushAsync();
        Assert(!settings.IsDirty, "M14 explicit Settings flush left dirty state behind.");
        await first.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var second = new MajoRuntimeBootstrap
        {
            Name = "SettingsRuntimeSecond",
            Configuration = configuration,
        };
        AddChild(second);
        await second.InitializationCompletion;
        var restored = second.Host!.Services.Get<ISettingsManager>();
        Assert(restored.Current.MasterVolume == 0.42f &&
                restored.Current.LettersPerSecond == 73 &&
                restored.RecoveryStatus.Kind == ScopedStateRecoveryKind.Loaded,
            "M14 Settings did not restore across a Runtime restart.");
        await second.Host.Services.Get<IStateStorage>().DeleteAsync("scopes/settings");
        await second.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}

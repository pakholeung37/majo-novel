using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.Settings;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateSettingsAndLocaleRuntimeAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.State!.StorageNamespace = $"majo-headless-settings-{Guid.NewGuid():N}";
        configuration.Localization = new LocalizationConfiguration
        {
            DefaultLocale = "en",
            SupportedLocales = ["en", "zh-CN"],
            FontScale = 1.0,
            LineSpacing = 0,
        };

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
        var localeManager = first.Host!.Services.Get<ILocaleManager>();
        var fonts = first.Host.Services.Get<IFontCatalog>();

        var dialogueResult = await localeManager.ChangeDialogueLocaleAsync("zh-CN");
        Assert(dialogueResult.Success && localeManager.DialogueLocale == "zh-CN", "Dialogue locale switch failed.");
        Assert(settings.Current.DialogueLocale == "zh-CN", "Dialogue locale was not committed to Settings.");

        var voiceResult = await localeManager.ChangeVoiceLocaleAsync("zh-CN");
        Assert(voiceResult.Success && localeManager.VoiceLocale == "zh-CN", "Voice locale switch failed.");
        Assert(settings.Current.VoiceLocale == "zh-CN", "Voice locale was not committed to Settings.");

        settings.Update(settings.Current with
        {
            FontScale = 1.25,
            LineSpacing = 4,
            MasterVolume = 0.42f,
            LettersPerSecond = 73,
            AutoExtraDelaySeconds = 1.75,
            AutoWaitForVoice = false,
        });
        Assert(audio.MasterVolume == 0.42f &&
                playback.ExtraDelay == TimeSpan.FromSeconds(1.75) &&
                !playback.WaitForVoice,
            "Settings did not immediately project Audio and Auto values.");
        Assert(Math.Abs(fonts.FontScale - 1.25) < 0.001 && fonts.LineSpacing == 4,
            "Settings did not project typography to the font catalog.");
        overlay.Open();
        Assert(overlay.IsOpen && input.ActiveHandleCount == baselineHandles + 1,
            "Settings UI did not acquire exactly one modal input scope.");
        overlay.Close();
        Assert(!overlay.IsOpen && input.ActiveHandleCount == baselineHandles,
            "Settings UI did not release its modal input scope.");
        await settings.FlushAsync();
        Assert(!settings.IsDirty, "Explicit Settings flush left dirty state behind.");
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
            "Settings did not restore across a Runtime restart.");
        var restoredLocales = second.Host.Services.Get<ILocaleManager>();
        var restoredFonts = second.Host.Services.Get<IFontCatalog>();

        Assert(restoredLocales.DialogueLocale == "zh-CN", "Dialogue locale was not restored.");
        Assert(restoredLocales.VoiceLocale == "zh-CN", "Voice locale was not restored.");
        Assert(Math.Abs(restoredFonts.FontScale - 1.25) < 0.001, "FontScale was not restored into IFontCatalog.");
        Assert(restoredFonts.LineSpacing == 4, "LineSpacing was not restored into IFontCatalog.");

        await second.Host.Services.Get<IStateStorage>().DeleteAsync("scopes/settings");
        await second.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}

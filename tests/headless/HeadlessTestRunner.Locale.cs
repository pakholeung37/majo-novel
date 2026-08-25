using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Localization;
using Majo.Runtime.Settings;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateLocaleRuntimeAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.State!.StorageNamespace = $"majo-headless-m17-{Guid.NewGuid():N}";
        configuration.Localization = new LocalizationConfiguration
        {
            DefaultLocale = "en",
            SupportedLocales = ["en", "zh-CN"],
            FontScale = 1.0,
            LineSpacing = 0,
        };

        var first = new MajoRuntimeBootstrap
        {
            Name = "LocaleRuntimeFirst",
            Configuration = configuration,
        };
        AddChild(first);
        await first.InitializationCompletion;

        var localeManager = first.Host!.Services.Get<ILocaleManager>();
        var fonts = first.Host.Services.Get<IFontCatalog>();
        var settings = first.Host.Services.Get<ISettingsManager>();

        Assert(localeManager.DialogueLocale == "en", "M17.5 dialogue locale should default to 'en'.");
        Assert(localeManager.VoiceLocale == "en", "M17.5 voice locale should default to 'en'.");
        Assert(localeManager.AvailableLocales.Count >= 2, "M17 AvailableLocales should contain configured locales.");

        var dialogueResult = await localeManager.ChangeDialogueLocaleAsync("zh-CN");
        Assert(dialogueResult.Success && localeManager.DialogueLocale == "zh-CN", "M17.5 dialogue locale switch failed.");
        Assert(localeManager.VoiceLocale == "en", "M17.5 dialogue locale switch changed voice locale.");
        Assert(settings.Current.DialogueLocale == "zh-CN", "M17.5 dialogue locale was not committed to Settings.");

        var voiceResult = await localeManager.ChangeVoiceLocaleAsync("zh-CN");
        Assert(voiceResult.Success && localeManager.VoiceLocale == "zh-CN", "M17.5 voice locale switch failed.");
        Assert(settings.Current.VoiceLocale == "zh-CN", "M17.5 voice locale was not committed to Settings.");

        // Typography changes
        settings.Update(settings.Current with
        {
            DialogueLocale = "zh-CN",
            VoiceLocale = "zh-CN",
            FontScale = 1.25,
            LineSpacing = 4,
        });

        Assert(Math.Abs(fonts.FontScale - 1.25) < 0.001, "M17 FontScale was not projected to IFontCatalog.");
        Assert(fonts.LineSpacing == 4, "M17 LineSpacing was not projected to IFontCatalog.");

        await settings.FlushAsync();
        await first.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // Restart to test persistence round-trip
        var second = new MajoRuntimeBootstrap
        {
            Name = "LocaleRuntimeSecond",
            Configuration = configuration,
        };
        AddChild(second);
        await second.InitializationCompletion;

        var restoredSettings = second.Host!.Services.Get<ISettingsManager>();
        var restoredLocales = second.Host.Services.Get<ILocaleManager>();
        var restoredFonts = second.Host.Services.Get<IFontCatalog>();

        Assert(restoredSettings.Current.DialogueLocale == "zh-CN", "M17.5 dialogue locale did not persist.");
        Assert(restoredSettings.Current.VoiceLocale == "zh-CN", "M17.5 voice locale did not persist.");
        Assert(restoredLocales.DialogueLocale == "zh-CN", "M17.5 dialogue locale was not restored.");
        Assert(restoredLocales.VoiceLocale == "zh-CN", "M17.5 voice locale was not restored.");
        Assert(Math.Abs(restoredSettings.Current.FontScale - 1.25) < 0.001, "M17 FontScale did not persist in settings across restart.");
        Assert(restoredSettings.Current.LineSpacing == 4, "M17 LineSpacing did not persist in settings across restart.");
        Assert(Math.Abs(restoredFonts.FontScale - 1.25) < 0.001, "M17 FontScale was not restored into IFontCatalog.");
        Assert(restoredFonts.LineSpacing == 4, "M17 LineSpacing was not restored into IFontCatalog.");

        await second.Host.Services.Get<IStateStorage>().DeleteAsync("scopes/settings");
        await second.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}

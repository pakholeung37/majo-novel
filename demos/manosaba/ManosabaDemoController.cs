#nullable enable

using System;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;
using Majo.Demos.Manosaba.Actors.Layered;

namespace Majo.Demos.Manosaba;

public partial class ManosabaDemoController : Node
{
    public override void _Ready()
    {
        RunDemoAsync();
    }

    private async void RunDemoAsync()
    {
        try
        {
            var progress = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-progress") >= 0;
            Trial00ProgressProbe? progressProbe = null;
            if (progress) { progressProbe = new Trial00ProgressProbe(); AddChild(progressProbe); }
            var configuration = MajoProjectConfiguration.CreateDefault();
            if (progress) configuration.Diagnostics!.RingBufferCapacity = 50000;
            var stress = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-stress") >= 0;
            if (progress || stress || System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-profile") >= 0)
            {
                configuration.Dialogue!.DefaultStartNode = "Act01_Chapter01_Trial00";
                if (!stress && !progress) AddChild(new Trial00PerformanceProbe());
            }
            configuration.Resources!.RootPath = "res://demos/manosaba/assets";
            configuration.Localization!.DefaultLocale = "zh-Hans";
            configuration.Localization.SupportedLocales = ["zh-Hans", "ja", "en", "zh-Hant"];
            configuration.Settings!.DialogueLocale = "zh-Hans";
            configuration.State!.StorageNamespace = "majo/manosaba";
            configuration.State.GameBuildId = "manosaba-v0.1";
            configuration.State.DialogueTravel.Enabled = true;
            configuration.Save!.Enabled = true;
            configuration.Diagnostics!.EnableOverlay = true;
            configuration.Diagnostics!.defaultOverlayVisible = false;
            configuration.Animation!.DefaultCharacterAppearanceAnimationId = "dissolve";
            configuration.Animation!.DefaultCharacterAnimationId = "fade";
            configuration.Diagnostics!.MinimumLevel = RuntimeLogLevel.Debug;
            configuration.Presentation!.PresenterScene =
                ResourceLoader.Load<PackedScene>(
                    "res://demos/manosaba/ManosabaPresentation.tscn");
            configuration.Presentation.Font = ResourceLoader.Load<Font>(
                "res://demos/manosaba/assets/fonts/SourceHanSerifSC.otf");
            configuration.Presentation.LettersPerSecond = 36;
            configuration.Actors!.ReferenceResolution = new Vector2I(2560, 1440);
            configuration.Actors.ScenePositionOffset = new Vector2(0, 10);
            configuration.Actors.LeftPosition = new Vector2(768, 1440);
            configuration.Actors.CenterPosition = new Vector2(1280, 1440);
            configuration.Actors.RightPosition = new Vector2(1792, 1440);


            var characterNames = ManosabaCharacterNameCatalog.Load(
                "res://demos/manosaba/assets/localization/character_names.json");
            var uiService = new ManosabaUiService();
            var runtime = new MajoRuntimeBootstrap
            {
                Name = "ManosabaRuntime",
                ConfigureScenePreparation = scenes => scenes.Register("act01_chapter01_trial00", ManosabaCourtPreparation.PrepareAsync),
                Configuration = configuration,
                ResolveSpeakerName = characterNames.Resolve,
                ConfigureServices = services =>
                {
                    services.AddSingleton<IManosabaUiService>(_ => uiService);
                },
                ConfigureCommands = commands =>
                {
                    ManosabaCommands.Register(commands);
                },
                ConfigureStateContributors = contributors =>
                {
                    contributors.Register(uiService);
                },
            };

            runtime.AddPlugin(new LayeredCharacterPlugin());

            AddChild(runtime);
            await runtime.InitializationCompletion;

            var services = runtime.Host!.Services;
            var marker = runtime.FindChild("MenuPanel", recursive: true, owned: false);
            if (marker?.GetParent() is not ManosabaPresentation presentation)
            {
                throw new InvalidOperationException(
                    "Manosaba presentation did not instantiate correctly.");
            }

            uiService.Attach(presentation);

            services.TryGet<IAudioManager>(out var audio);
            presentation.Bind(
                services.Get<IPlaybackModeManager>(),
                services.Get<IHistoryManager>(),
                services.Get<ISaveLoadOverlay>(),
                services.Get<ISettingsOverlay>(),
                audio);

            var dialogue = services.Get<IDialoguePlayer>();
            Trial00StressProbe? stressProbe = null;
            if (stress)
            {
                stressProbe = new Trial00StressProbe();
                AddChild(stressProbe);
            }
            progressProbe?.Attach(runtime);
            await dialogue.StartAsync();
            if (progressProbe is not null) await progressProbe.RunAsync(runtime);
            if (stressProbe is not null) await stressProbe.RunAsync(runtime);
        }
        catch (Exception exception)
        {
            GD.PushError($"Manosaba Demo failed: {exception}");
        }
    }
}

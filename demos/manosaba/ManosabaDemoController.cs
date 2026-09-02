#nullable enable

using System;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

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
            var configuration = MajoProjectConfiguration.CreateDefault();
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

            // await services.Get<IScriptPreloadManager>().PrepareAsync(
            // 	CreateOpeningPreloadWindow());

            var dialogue = services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
        }
        catch (Exception exception)
        {
            GD.PushError($"Manosaba Demo failed: {exception}");
        }
    }

    private static ScriptPreloadWindow CreateOpeningPreloadWindow()
    {
        const long mib = 1024L * 1024L;
        var resources = new ResourcePreloadManifest(
            "manosaba-act01-adv02-opening-resources",
            [
                ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("backgrounds/Still_490_001"),
                    estimatedBytes: 32 * mib),
                ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("backgrounds/Background_001_001"),
                    estimatedBytes: 32 * mib),
                ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("characters/Ema/Ema_Angle01"),
                    estimatedBytes: 256 * mib),
                ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("characters/Hiro/002_Hiro_Angle01"),
                    estimatedBytes: 128 * mib),
                ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("characters/Warden/1"),
                    estimatedBytes: 12 * mib),
            ]);
        return new ScriptPreloadWindow(
            "manosaba-act01-adv02-opening",
            resources,
            [
                new ActorWarmupRequest(
                    "Ema",
                    ActorFactoryIds.Character,
                    ["Surprised1", "Normal1,ArmR3", "Fearful1,ArmR1"]),
                new ActorWarmupRequest(
                    "Hiro",
                    ActorFactoryIds.Character,
                    ["Angry1", "Surprised1", "Angry2"]),
                new ActorWarmupRequest(
                    "Warden",
                    ActorFactoryIds.Character,
                    ["1"]),
            ]);
    }
}

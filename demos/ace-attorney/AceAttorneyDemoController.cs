#nullable enable

using System;
using Godot;
using Majo.Demos.AceAttorney.Actors;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Direction.Execution;
using YarnSpinnerGodot;

namespace Majo.Demos.AceAttorney;

public partial class AceAttorneyDemoController : Node
{
    public override void _Ready() => RunDemoAsync();

    private async void RunDemoAsync()
    {
        try
        {
            var settings = MajoProjectConfiguration.CreateDefault();
            var automatic = Array.IndexOf(OS.GetCmdlineUserArgs(), "--ace-attorney-auto") >= 0;
            var resolution = ReadBackgroundResolution();
            var window = GetWindow();
            window.ContentScaleSize = resolution;
            window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            window.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
            window.Size = resolution;
            settings.Resources!.RootPath = "res://demos/ace-attorney/assets";
            settings.Localization!.DefaultLocale = "zh-Hans";
            settings.Localization.SupportedLocales = ["zh-Hans"];
            settings.Settings!.DialogueLocale = "zh-Hans";
            settings.State!.StorageNamespace = "majo/ace-attorney-demo";
            settings.State.GameBuildId = "ace-attorney-demo-v1";
            settings.State.DialogueTravel.Enabled = true;
            settings.Save!.Enabled = true;
            settings.Diagnostics!.EnableOverlay = !automatic;
            settings.Diagnostics.defaultOverlayVisible = false;
            settings.Presentation!.LettersPerSecond = automatic ? 240 : 32;
            settings.Presentation.Enabled = true;
            settings.Actors!.ReferenceResolution = resolution;
            settings.Actors.LeftPosition = new Vector2(resolution.X * 0.35f, resolution.Y);
            settings.Actors.CenterPosition = new Vector2(resolution.X * 0.5f, resolution.Y);
            settings.Actors.RightPosition = new Vector2(resolution.X * 0.65f, resolution.Y);

            foreach (var id in new[] { "Phoenix", "Miles", "Judge", "Maya", "Dick" })
            {
                settings.Actors.Actors.Add(new ActorDefinitionConfiguration
                {
                    Id = id,
                    FactoryId = SpriteSheetCharacterPlugin.Factory,
                    FactoryConfiguration = new SpriteSheetCharacterConfiguration
                    {
                        ManifestPath = $"res://demos/ace-attorney/assets/characters/{id}/sprite_character.json",
                        InitialPosition = CharacterPositionPreset.Center,
                    },
                });
            }

            var registry = new SpriteSheetActorRegistry();
            var probe = automatic ? new AceAttorneyDialogueProbe() : null;
            if (probe is not null) AddChild(probe);
            var runtime = new MajoRuntimeBootstrap
            {
                Name = "AceAttorneyRuntime",
                Configuration = settings,
                ConfigureDialoguePresenters = presenters =>
                {
                    presenters.Register(new SpriteSheetSpeechPresenter(registry));
                    if (probe is not null) presenters.Register(probe);
                },
            };
            runtime.AddPlugin(new SpriteSheetCharacterPlugin(registry));
            AddChild(runtime);
            await runtime.InitializationCompletion;
            var linePresenter = runtime.FindChild("LinePresenter", recursive: true, owned: false)
                as LinePresenter ?? throw new InvalidOperationException("Line presenter is missing.");
            var blip = new AudioStreamPlayer
            {
                Name = "DialogueBlip",
                Stream = ResourceLoader.Load<AudioStream>(
                    "res://demos/ace-attorney/assets/audio/sfx/Blip.wav") ??
                    throw new InvalidOperationException("Dialogue blip audio is missing."),
            };
            linePresenter.AddChild(blip);
            var blipHandler = new DialogueBlipHandler(blip);
            linePresenter.ActionMarkupHandlers.Add(blipHandler);
            var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
            if (automatic)
            {
                var completion = await dialogue.Completion;
                if (completion.Kind != DialogueCompletionKind.Completed || probe!.LineCount < 15 ||
                    blipHandler.PlayCount == 0 ||
                    runtime.Host.Services.Get<IDirectionTransactionCoordinator>().State.Stages.Count == 0)
                    throw new InvalidOperationException("Sprite sheet demo did not complete its smoke script.");
                GD.Print($"ACE_ATTORNEY_DEMO_OK: {probe.LineCount} lines, {blipHandler.PlayCount} blips.");
                await runtime.RequestShutdownAsync();
                if (GodotObject.IsInstanceValid(blip))
                {
                    blip.Stop();
                    blip.Stream = null;
                    blip.QueueFree();
                }
                probe.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GetTree().Quit(0);
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Ace Attorney demo failed: {exception}");
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ace-attorney-auto") >= 0)
                GetTree().Quit(1);
        }
    }

    private static Vector2I ReadBackgroundResolution()
    {
        using var background = Image.LoadFromFile(
            "res://demos/ace-attorney/assets/backgrounds/Defense.jpg") ??
            throw new InvalidOperationException("Ace Attorney defense background is missing.");
        return background.GetSize();
    }
}

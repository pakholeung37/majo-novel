#nullable enable

using System;
using System.Globalization;
using Godot;
using Majo.Demos.Live2D.Actors;
using Majo.Demos.Recording;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Direction.Execution;

namespace Majo.Demos.Live2D;

public partial class Live2DDemoController : Node
{
    public override void _Ready() => RunDemoAsync();

    private async void RunDemoAsync()
    {
        try
        {
            var configuration = CreateConfiguration();
            var automatic = Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-auto") >= 0;
            var stress = Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-stress") >= 0;
            configuration.Presentation!.Enabled = !automatic && !stress;
            configuration.Diagnostics!.EnableOverlay = !automatic && !stress;
            ShowcaseRecording.Current?.Configure(configuration);
            Live2DDialogueProbe? probe = null;
            if (automatic)
            {
                var capture = Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-auto-capture") >= 0;
                probe = new Live2DDialogueProbe
                {
                    Name = "Live2DDialogueProbe",
                    CaptureDirectory = capture
                        ? ProjectSettings.GlobalizePath("res://artifacts/live2d-yarn/" +
                            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
                        : null,
                };
                AddChild(probe);
            }
            var runtime = new MajoRuntimeBootstrap
            {
                Name = "Live2DRuntime",
                Configuration = configuration,
            };
            if (probe is not null)
            {
                runtime.ConfigureDialoguePresenters += presenters => presenters.Register(probe);
            }
            runtime.AddPlugin(new Live2DCharacterPlugin());
            if (ShowcaseRecording.Current is { } recording)
                runtime.ConfigureDialoguePresenters += presenters => presenters.Register(recording);
            AddChild(runtime);
            await runtime.InitializationCompletion;
            if (stress)
            {
                var stressProbe = new Live2DStressProbe { Name = "Live2DStressProbe" };
                AddChild(stressProbe);
                await stressProbe.RunAsync(runtime, ParseStressActorCount());
                return;
            }
            var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
            ShowcaseRecording.Current?.Attach(runtime);
            await dialogue.StartAsync();
            if (automatic)
            {
                var completion = await dialogue.Completion;
                if (completion.Kind != DialogueCompletionKind.Completed ||
                    probe!.LineCount < 10 ||
                    runtime.Host.Services.Get<IDirectionTransactionCoordinator>()
                        .State.Stages.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Live2D automatic demo did not reach its expected final state.");
                }

                GD.Print($"LIVE2D_DEMO_OK: {probe.LineCount} lines completed.");
                if (probe.CaptureDirectory is not null)
                    GD.Print($"LIVE2D_CAPTURE_COMPLETE {probe.CaptureDirectory}");
                await runtime.RequestShutdownAsync();
                probe.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GetTree().Quit(0);
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Live2D demo failed: {exception}");
            ShowcaseRecording.Current?.Fail(exception);
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-auto") >= 0 ||
                Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-stress") >= 0)
            {
                GetTree().Quit(1);
            }
        }
    }

    private static int ParseStressActorCount()
    {
        const string prefix = "--live2d-stress-count=";
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(argument[prefix.Length..], out var count) &&
                count >= Live2DStressProbe.MinimumActorCount)
                return count;
            throw new ArgumentException(
                $"{prefix}<count> must be at least {Live2DStressProbe.MinimumActorCount}.");
        }
        return Live2DStressProbe.MinimumActorCount;
    }

    private static MajoProjectConfiguration CreateConfiguration()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://demos/live2d/assets";
        configuration.Localization!.DefaultLocale = "zh-Hans";
        configuration.Localization.SupportedLocales = ["zh-Hans"];
        configuration.Settings!.DialogueLocale = "zh-Hans";
        configuration.State!.StorageNamespace = "majo/live2d-demo";
        configuration.State.GameBuildId = "live2d-demo-v1";
        configuration.State.DialogueTravel.Enabled = true;
        configuration.Save!.Enabled = true;
        configuration.Diagnostics!.EnableOverlay = true;
        configuration.Diagnostics.defaultOverlayVisible = false;
        configuration.Diagnostics.MinimumLevel = RuntimeLogLevel.Debug;
        configuration.Presentation!.LettersPerSecond = 30;

        configuration.Actors!.ReferenceResolution = new Vector2I(1920, 1080);
        configuration.Actors.LeftPosition = new Vector2(610, 1040);
        configuration.Actors.CenterPosition = new Vector2(960, 1040);
        configuration.Actors.RightPosition = new Vector2(1310, 1040);


        var actor = new ActorDefinitionConfiguration
        {
            Id = "mao",
            FactoryId = Live2DActorIds.Factory,
            FactoryConfiguration = new Live2DCharacterConfiguration
            {
                ModelPath = "res://demos/live2d/assets/characters/mao/runtime/mao_pro.model3.json",
                DefaultAppearance = "neutral",
                InitialPosition = CharacterPositionPreset.Center,
                DisplayHeight = 1800,
                VisualOffset = new Vector2(0, -250),
                Expressions = new Godot.Collections.Dictionary<string, string>
                {
                    ["neutral"] = "exp_01",
                    ["smile"] = "exp_02",
                    ["sleepy"] = "exp_03",
                    ["sparkle"] = "exp_04",
                    ["serious"] = "exp_05",
                    ["blush"] = "exp_06",
                    ["shocked"] = "exp_07",
                    ["smug"] = "exp_08",
                },
                Motions = new Godot.Collections.Dictionary<string, string>
                {
                    ["idle"] = "Idle:0",
                    ["wave"] = ":0",
                    ["cheer"] = ":1",
                    ["puzzled"] = ":2",
                    ["magic"] = ":3",
                    ["panic"] = ":4",
                    ["flourish"] = ":5",
                },
            },
        };
        configuration.Actors.Actors.Add(actor);
        return configuration;
    }
}

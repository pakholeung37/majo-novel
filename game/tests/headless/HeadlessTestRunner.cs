using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Demo;
using Majo.YarnBridge;
using Majo.Runtime;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner : Node
{
    private const double SuiteTimeoutSeconds = 15;
    private const string ForcedFailureArgument = "--majo-force-failure";

    public override void _Ready()
    {
        RunAndExitAsync();
    }

    private async void RunAndExitAsync()
    {
        try
        {
            await RunWithTimeoutAsync(RunSuiteAsync(), SuiteTimeoutSeconds);
            await FinalizeEngineObjectsAsync();
            GD.Print("Majo Runtime headless tests passed.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"Majo Runtime headless tests failed: {exception}");
            await FinalizeEngineObjectsAsync();
            GetTree().Quit(1);
        }
    }

    private async Task RunSuiteAsync()
    {
        if (OS.GetCmdlineUserArgs().Contains(ForcedFailureArgument, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Intentional headless failure probe.");
        }

        var dependencyStatus = YarnDependencyValidator.ValidateProject(
            ProjectSettings.GlobalizePath("res://"));
        Assert(dependencyStatus.IsCompatible, dependencyStatus.Message);

        ValidateConfigurationDiagnostics();
        await ValidateAudioRuntimeAsync();
        await ValidateInputRuntimeAsync();
        await ValidateYarnVmStateBridgeAsync();
        await ValidateDefaultPresentationAsync();
        await ValidateSuccessfulRuntimeLifecycleAsync();
        await ValidateLifecycleTimeoutAsync();
        await ValidateOperationKernelAsync();
        await ValidateActorSystemAsync();
        await ValidateYarnCommandAdapterAsync();
        await ValidateM5PlayableLoopAsync();
        await ValidateFailedConfigurationCleanupAsync();
    }

    private async Task ValidateAudioRuntimeAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        var loader = new HeadlessAudioClipLoader()
            .Register("calm")
            .Register("battle")
            .Register("hit")
            .Register("en/line-1")
            .Register("en/line-2");
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "AudioRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureServices = services => services.ReplaceSingleton<IAudioManager>(
            resolver => new AudioManager(
                new GodotAudioBackend(bootstrap),
                resolver.Get<IOperationScheduler>(),
                loader,
                loader,
                loader,
                new AudioSettings()),
            typeof(IOperationScheduler));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var audio = bootstrap.Host!.Services.Get<IAudioManager>();
        var operations = bootstrap.Host.Services.Get<IOperationScheduler>();
        var audioHost = bootstrap.GetNode("MajoAudioHost");
        await audio.PlayBgmAsync("calm", "music");
        var crossfade = await audio.PlayBgmAsync(
            "battle",
            "music",
            fade: new AudioFade(0.08, "headless-crossfade", "headless-audio"));
        Assert(audioHost.GetChildCount() == 2,
            "M9 BGM crossfade did not keep old and new Godot players concurrently.");
        await crossfade.FadeOperation.Completion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(audioHost.GetChildCount() == 1 &&
                audio.BgmTracks.Single().ClipId == "battle",
            "M9 BGM crossfade did not release the old Godot player.");

        await audio.PlaySfxAsync("hit", "left");
        await audio.PlaySfxAsync("hit", "right");
        Assert(audio.SfxInstances.Count == 2 && audioHost.GetChildCount() == 3,
            "M9 named SFX instances did not overlap in the Godot backend.");

        var firstVoice = await audio.PlayVoiceAsync("line-1");
        var secondVoice = await audio.PlayVoiceAsync("line-2");
        Assert(firstVoice.Playback.State == AudioPlaybackState.Stopped &&
                ReferenceEquals(audio.CurrentVoice, secondVoice.Playback),
            "M9 single-channel Voice did not preempt the previous voice.");

        var cancelled = await audio.PlayBgmAsync(
            "calm",
            "music",
            fade: new AudioFade(1, "headless-cancel"));
        await operations.CancelAsync(OperationTarget.ForId("headless-cancel"));
        Assert(cancelled.Playback.State == AudioPlaybackState.Stopped &&
                audio.BgmTracks.Single().ClipId == "battle",
            "M9 cancel did not restore the pre-crossfade BGM terminal state.");

        var completed = await audio.PlayBgmAsync(
            "calm",
            "music",
            fade: new AudioFade(1, "headless-complete"));
        await operations.CompleteAsync(OperationTarget.ForId("headless-complete"));
        Assert(completed.Playback.State == AudioPlaybackState.Playing &&
                audio.BgmTracks.Single().ClipId == "calm",
            "M9 complete did not force the BGM crossfade target state.");

        await bootstrap.ResetRuntimeAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(audioHost.GetChildCount() == 0 &&
                audio.BgmTracks.Count == 0 &&
                audio.SfxInstances.Count == 0 &&
                audio.CurrentVoice is null &&
                operations.ActiveOperations.Count == 0,
            "M9 Reset left audio players or Operations alive.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap),
            "M9 shutdown left the Runtime or audio host in the scene tree.");
    }

    private async Task ValidateYarnVmStateBridgeAsync()
    {
        var presenter = new BlockingBridgePresenter();
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "YarnVmBridge";
        configuration.Presentation!.Enabled = false;

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "YarnVmBridgeRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var dialogue = bootstrap.Host!.Services.Get<IDialoguePlayer>();
        var backend = bootstrap.Host.Services.Get<IYarnDialogueBackend>();
        var runner = backend.Runner;
        var bridge = new YarnVmStateBridge();

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presenter.Lines.Count == 1, 2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var snapshot = bridge.CaptureAtLine(
            runner.Dialogue,
            "line:bridge-headless-first");
        Assert(snapshot.ExecutionState == "WaitingForContinue" &&
                snapshot.OperandStackDepth == 0 &&
                snapshot.CallStackDepth == 0 &&
                snapshot.PendingOptionCount == 0,
            "Yarn VM Bridge captured an unsafe Godot DialogueRunner boundary.");

        await dialogue.StopAsync();
        var injectionCount = 0;
        var injectReplay = Callable.From(() =>
        {
            var project = runner.YarnProject ?? throw new InvalidOperationException(
                "DialogueRunner had no YarnProject during replay injection.");
            bridge.InjectReplayAfterNodeSelected(
                runner.Dialogue,
                project.Program,
                snapshot);
            injectionCount++;
        });
        runner.Connect(DialogueRunner.SignalName.onDialogueStart, injectReplay);

        await dialogue.StartAsync();
        var replayCompletion = dialogue.Completion;
        await WaitForConditionAsync(() => presenter.Lines.Count == 2, 2);
        Assert(injectionCount == 1 &&
                presenter.Lines[0].Id == "line:bridge-headless-first" &&
                presenter.Lines[1].Id == "line:bridge-headless-first",
            "DialogueRunner did not replay the captured line after VM state injection.");

        dialogue.RequestNextLine();
        await WaitForConditionAsync(() => presenter.Lines.Count == 3, 2);
        Assert(presenter.Lines[2].Id == "line:bridge-headless-second",
            "DialogueRunner did not continue to the line after the restored snapshot.");

        dialogue.RequestNextLine();
        await replayCompletion;
        Assert(dialogue.State == DialogueSessionState.Idle,
            "Restored DialogueRunner session did not complete normally.");

        runner.Disconnect(DialogueRunner.SignalName.onDialogueStart, injectReplay);
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap) && !IsInstanceValid(runner),
            "Yarn VM Bridge headless validation leaked Runtime nodes.");
    }

    private async Task ValidateInputRuntimeAsync()
    {
        var existingMenuAction = new StringName(
            MajoInputActions.ToGodotAction(MajoInputActions.Menu));
        if (InputMap.HasAction(existingMenuAction))
        {
            InputMap.EraseAction(existingMenuAction);
        }

        InputMap.AddAction(existingMenuAction);
        InputMap.ActionAddEvent(existingMenuAction, new InputEventKey
        {
            PhysicalKeycode = Key.F12,
        });

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "InputRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
        };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var manager = bootstrap.Host!.Services.Get<IInputManager>();
        var receiver = bootstrap.GetNode("MajoInput");
        Assert(InputMap.ActionGetEvents(existingMenuAction).Count == 1 &&
                InputMap.ActionGetEvents(existingMenuAction)[0] is InputEventKey menuKey &&
                menuKey.PhysicalKeycode == Key.F12,
            "M7 default InputMap installation overwrote an existing host binding.");
        foreach (var action in MajoInputActions.All)
        {
            Assert(InputMap.HasAction(new StringName(MajoInputActions.ToGodotAction(action))),
                $"M7 did not install missing InputMap action '{action}'.");
        }

        var owner = new Node { Name = "InputOwner" };
        AddChild(owner);
        var activations = new List<InputActivation>();
        var scope = manager.AcquireScope(
            owner,
            new InputScopeOptions(
                MajoInputActions.All,
                name: "headless-input"));
        scope.Activated += activations.Add;

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = true,
        });
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = false,
        });
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = true,
        });
        await WaitForConditionAsync(() => activations.Count == 1, 1);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Godot.Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
        });
        await WaitForConditionAsync(() => activations.Count == 2, 1);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Godot.Input.ParseInputEvent(new InputEventJoypadButton
        {
            ButtonIndex = JoyButton.A,
            Pressed = true,
        });
        await WaitForConditionAsync(() => activations.Count == 3, 1);

        Assert(activations.Select(activation => activation.Action).All(
                action => action == MajoInputActions.Advance),
            "M7 keyboard, mouse and gamepad did not resolve to the same logical action.");
        Assert(activations.Select(activation => activation.Device).SequenceEqual(new[]
            {
                InputDeviceKind.Keyboard,
                InputDeviceKind.Mouse,
                InputDeviceKind.Gamepad,
            }),
            "M7 headless input injection reported incorrect device kinds.");

        var expectedSequence = new[]
        {
            MajoInputActions.Advance,
            MajoInputActions.Cancel,
            MajoInputActions.OptionUp,
            MajoInputActions.OptionDown,
            MajoInputActions.Confirm,
        };
        activations.Clear();
        await InjectSequenceAsync(
        [
            new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true },
            new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true },
            new InputEventKey { PhysicalKeycode = Key.Up, Pressed = true },
            new InputEventKey { PhysicalKeycode = Key.Down, Pressed = true },
            new InputEventKey { PhysicalKeycode = Key.Enter, Pressed = true },
        ]);
        await InjectSequenceAsync(
        [
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
            },
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
            },
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.WheelUp,
                Pressed = true,
            },
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.WheelDown,
                Pressed = true,
            },
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = true,
            },
        ]);
        await InjectSequenceAsync(
        [
            new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.B, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.DpadUp, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = true },
        ]);
        for (var deviceIndex = 0; deviceIndex < 3; deviceIndex++)
        {
            var sequence = activations
                .Skip(deviceIndex * expectedSequence.Length)
                .Take(expectedSequence.Length)
                .Select(activation => activation.Action);
            Assert(sequence.SequenceEqual(expectedSequence),
                "M7 keyboard, mouse and gamepad produced different logical sequences.");
        }

        var advanceAction = new StringName(
            MajoInputActions.ToGodotAction(MajoInputActions.Advance));
        var overlappingEnterEvent = new InputEventKey
        {
            PhysicalKeycode = Key.Enter,
        };
        InputMap.ActionAddEvent(advanceAction, overlappingEnterEvent);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Enter,
            Pressed = true,
        });
        await WaitForConditionAsync(() => activations.Count == 16, 1);
        Assert(activations[^1].Action == MajoInputActions.Advance,
            "M7 emitted both advance and confirm for one overlapping physical binding.");

        async Task InjectSequenceAsync(IEnumerable<InputEvent> inputEvents)
        {
            foreach (var inputEvent in inputEvents)
            {
                var expectedCount = activations.Count + 1;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Godot.Input.ParseInputEvent(inputEvent);
                await WaitForConditionAsync(() => activations.Count == expectedCount, 1);
            }
        }

        owner.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(scope.IsDisposed && manager.ActiveHandleCount == 0,
            "M7 did not release an input Scope when its Node owner exited the tree.");

        var resetHandle = manager.AcquireMuter(new object());
        await bootstrap.ResetRuntimeAsync();
        Assert(resetHandle.IsDisposed && manager.ActiveHandleCount == 0,
            "M7 Reset left an input Muter active.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(receiver),
            "M7 Runtime shutdown left the Godot input receiver behind.");
        Assert(!IsInstanceValid(bootstrap), "M7 Input Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(owner), "M7 input owner node leaked after cleanup.");
        InputMap.ActionEraseEvent(advanceAction, overlappingEnterEvent);
        InputMap.EraseAction(existingMenuAction);
    }

    private async Task ValidateDefaultPresentationAsync()
    {
        var customScene = new PackedScene();
        var defaultScene = ResourceLoader.Load<PackedScene>(
            DialoguePresentationConfiguration.DefaultScenePath);
        Assert(defaultScene is not null, "M8 default Presenter scene could not be loaded.");
        var templateRoot = defaultScene!.Instantiate<Control>();
        templateRoot.SetMeta("m8_custom_scene", true);
        var packResult = customScene.Pack(templateRoot);
        templateRoot.Free();
        Assert(packResult == Error.Ok, "M8 custom Presenter scene could not be packed.");

        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "M8Presentation";
        configuration.Presentation!.Enabled = true;
        configuration.Presentation.PresenterScene = customScene;
        configuration.Presentation.LettersPerSecond = 1;
        configuration.Presentation.UseFadeEffect = false;

        var handlerOrder = new List<string>();
        var firstHandler = new M8MarkupProbe("first", handlerOrder);
        var secondHandler = new M8MarkupProbe("second", handlerOrder);
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "M8Runtime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialogueMarkupHandlers += handlers => handlers
            .Register(firstHandler)
            .Register(secondHandler);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var dialogue = bootstrap.Host!.Services.Get<IDialoguePlayer>();
        var presentation = bootstrap.Host.Services.Get<IDialoguePresentation>();
        var input = bootstrap.Host.Services.Get<IInputManager>();
        var backend = bootstrap.Host.Services.Get<IYarnDialogueBackend>();
        var presentationRoot = bootstrap.FindChild(
            "MajoDefaultDialoguePresentation",
            recursive: true,
            owned: false) as Control;
        Assert(presentationRoot is not null &&
                presentationRoot.HasMeta("m8_custom_scene") &&
                presentationRoot.GetMeta("m8_custom_scene").AsBool(),
            "M8 ignored the configured Presenter PackedScene override.");
        var nativeLinePresenter = presentationRoot!.FindChild(
            "LinePresenter",
            recursive: true,
            owned: false) as LinePresenter;
        Assert(nativeLinePresenter?.Typewriter is LetterTypewriter,
            "M8 did not reuse Yarn's native letter Typewriter.");
        Assert(backend.Runner.dialoguePresenters is not null &&
                backend.Runner.dialoguePresenters.OfType<LineAdvancer>().Count() == 0,
            "M8 registered Yarn LineAdvancer and bypassed M7 input ownership.");

        await dialogue.StartAsync();
        await WaitForConditionAsync(
            () => presentation.IsLineActive && !presentation.IsLineDisplayComplete,
            2);
        Assert(dialogue.CurrentLineId == "line:m8-first",
            "M8 did not publish the first line through the M6 facade.");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = true,
        });
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = true,
        });
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Space,
            Pressed = false,
        });
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        Assert(dialogue.CurrentLineId == "line:m8-first" &&
                !dialogue.IsPresentingOptions,
            "M8 allowed one same-frame Advance to both hurry and dismiss a line.");
        Assert(firstHandler.SawCancelledCharacterToken,
            "M8 markup handler did not receive Yarn's hurried-line cancellation token.");

        await InjectKeyAsync(Key.Space);
        await WaitForConditionAsync(
            () => dialogue.IsPresentingOptions && presentation.IsPresentingOptions,
            2);
        var visibleOptionItems = presentationRoot.FindChild(
                "OptionContainer",
                recursive: true,
                owned: false)
            .GetChildren()
            .OfType<OptionItem>()
            .Where(option => option.Visible)
            .ToArray();
        Assert(visibleOptionItems.Length == 2 && visibleOptionItems.All(option =>
            {
                var button = option.FindChild("Button", recursive: true, owned: false)
                    as BaseButton;
                return button is not null && !button.IsConnected(
                    BaseButton.SignalName.Pressed,
                    Callable.From(option.InvokeOptionSelected));
            }),
            "M8 native OptionItem buttons bypassed M7 modal input ownership.");
        await InjectKeyAsync(Key.Down);
        await InjectKeyAsync(Key.Enter);
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m8-second-option" &&
                presentation.IsLineActive,
            2);

        await InjectKeyAsync(Key.Space);
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        await InjectKeyAsync(Key.Space);
        var completion = await dialogue.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert(completion.Kind == DialogueCompletionKind.Completed,
            "M8 input-driven dialogue did not complete naturally.");
        Assert(!presentation.IsLineActive && !presentation.IsPresentingOptions,
            "M8 retained active Presentation state after dialogue completion.");
        Assert(input.ActiveHandleCount == 0,
            "M8 retained line or modal option input ownership after completion.");
        Assert(handlerOrder.Take(2).SequenceEqual(new[]
            {
                "first:prepared",
                "second:prepared",
            }),
            "M8 markup handlers did not run in registration order.");
        Assert(firstHandler.PreparedTexts.Count >= 2,
            "M8 did not project Yarn markup for every displayed line.");
        var firstText = firstHandler.PreparedTexts[0];
        var unknownMarkup = firstText.Attributes.SingleOrDefault(
            attribute => attribute.Name == "mystery");
        Assert(unknownMarkup is not null &&
                unknownMarkup.Properties["count"].IntegerValue == 3 &&
                unknownMarkup.Properties["enabled"].BooleanValue &&
                unknownMarkup.Properties["label"].StringValue == "alpha" &&
                !firstText.Text.Contains("mystery", StringComparison.Ordinal),
            "M8 failed to preserve unknown Yarn markup information in its facade.");

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presentation.IsLineActive, 2);
        var stoppedLineCompletion = dialogue.Completion;
        await dialogue.StopAsync();
        Assert((await stoppedLineCompletion).Kind == DialogueCompletionKind.Stopped &&
                !presentation.IsLineActive && input.ActiveHandleCount == 0,
            "M8 did not cancel and release an active native line Presenter.");

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presentation.IsLineActive, 2);
        backend.RequestHurryUp();
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        backend.RequestNextLine();
        await WaitForConditionAsync(() => presentation.IsPresentingOptions, 2);
        var stoppedOptionsCompletion = dialogue.Completion;
        await dialogue.StopAsync();
        Assert((await stoppedOptionsCompletion).Kind == DialogueCompletionKind.Stopped &&
                !presentation.IsPresentingOptions && input.ActiveHandleCount == 0,
            "M8 did not cancel and release modal option Presentation.");

        var presentationNode = presentationRoot;
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(presentationNode),
            "M8 Presenter PackedScene survived Runtime shutdown.");
        Assert(!IsInstanceValid(bootstrap), "M8 Runtime node leaked after shutdown.");
        configuration.Presentation.PresenterScene = null;
        customScene.Dispose();

        async Task InjectKeyAsync(Key key)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = key,
                Pressed = true,
            });
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = key,
                Pressed = false,
            });
        }
    }

    private async Task ValidateM5PlayableLoopAsync()
    {
        var presenter = new M5DialogueProbe
        {
            Name = "M5DialogueProbe",
            PreferredOptionIndex = 1,
        };
        AddChild(presenter);
        var portraitHost = new Node2D { Name = "M5PortraitHost" };
        AddChild(portraitHost);
        var configuration = M5DemoComposition.CreateConfiguration();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "M5Runtime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        var customFactory = M5DemoComposition.ConfigureRuntime(
            bootstrap,
            portraitHost,
            configuration.Actors!);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var resources = bootstrap.Host!.Services.Get<IResourceManager>();
        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var caseService = bootstrap.Host.Services.Get<M5CaseService>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var variables = bootstrap.Host.Services.Get<IDialogueVariables>();
        var ownedRunner = bootstrap.Host.Services.Get<IYarnDialogueBackend>().Runner;
        var sessionEvents = new List<string>();
        dialogue.Started += _ => sessionEvents.Add("started");
        dialogue.Completed += _ => sessionEvents.Add("completed");
        dialogue.Stopped += _ => sessionEvents.Add("stopped");
        var joinedAtFinalState = false;
        var waitedAtFinalState = false;
        presenter.LinePresented += line =>
        {
            var hero = manager.Get<ICharacterActor>("hero");
            if (line.Id == "line:m5-after-join")
            {
                var backdrop = manager.Get<IBackgroundActor>("backdrop");
                joinedAtFinalState =
                    hero.Appearance == "calm" &&
                    hero.Position.IsEqualApprox(configuration.Actors.LeftPosition) &&
                    backdrop.Appearance == "courtroom" &&
                    scheduler.ActiveOperations.Count == 0;
            }
            else if (line.Id == "line:m5-after-wait")
            {
                waitedAtFinalState =
                    hero.Appearance == "determined" &&
                    hero.Position.IsEqualApprox(configuration.Actors.CenterPosition);
            }
        };

        variables.SetString("headlessProbe", "ready");
        Assert(variables.Get("$headlessProbe").StringValue == "ready",
            "Yarn-free dialogue variables did not preserve a string value.");
        Assert(ownedRunner.VariableStorage.TryGetValue<string>(
                "$headlessProbe",
                out var yarnVariable) && yarnVariable == "ready",
            "Yarn-free dialogue variables were not bridged into Yarn storage.");

        await dialogue.StartAsync();
        Assert(dialogue.State == DialogueSessionState.Running,
            "M6 StartAsync returned before the dialogue entered Running.");
        Assert(dialogue.CurrentProjectId == "main" && dialogue.CurrentNode == "Start",
            "M6 dialogue did not expose its resolved project and node.");
        await WaitForConditionAsync(() => scheduler.ActiveOperations.Count >= 2, 2);
        Assert(presenter.Lines.Count == 0,
            "M5 dialogue advanced before its parallel opening animations joined.");
        await dialogue.Completion;

        Assert(dialogue.State == DialogueSessionState.Idle,
            "Naturally completed M6 dialogue did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(new[] { "started", "completed" }),
            "M6 natural dialogue session events were out of order.");
        Assert(joinedAtFinalState,
            "M5 parallel animation join did not expose both final Actor states.");
        Assert(waitedAtFinalState,
            "M5 wait=true animation did not finish before the next line.");
        Assert(presenter.Lines.Select(line => line.Id).SequenceEqual(new[]
            {
                "line:m5-after-join",
                "line:m5-after-wait",
                "line:m5-branch-evidence",
                "line:m5-finished",
            }),
            "M5 automatic option selection entered the wrong Yarn branch.");
        Assert(presenter.Lines[0].Speaker == "Narrator" &&
                !presenter.Lines[0].Text.Text.StartsWith("Narrator:", StringComparison.Ordinal),
            "M6 line projection did not separate the speaker from dialogue text.");
        Assert(dialogue.CurrentLineId == "line:m5-finished",
            "M6 dialogue did not retain the most recently presented line ID.");
        Assert(caseService.SelectedRoute == "evidence",
            "M5 custom command did not update the custom service.");
        Assert(caseService.InitializeCount == 1,
            "M5 custom service was not initialized exactly once.");
        Assert(customFactory.CreateCount == 1,
            "M5 custom Actor factory did not create the hero exactly once.");
        Assert(manager.Get<ICharacterActor>("hero").SceneNode.GetParent() == portraitHost,
            "M5 custom Actor factory did not use its external host.");
        Assert(presenter.Events
                .Where(entry => entry.Kind == "dialogue")
                .Select(entry => entry.Value)
                .SequenceEqual(new[] { "started", "completed" }),
            "M5 Presenter probe did not observe the complete dialogue lifecycle.");
        Assert(presenter.Events.Any(entry => entry.Kind == "option"),
            "M5 Presenter probe did not observe an option selection.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "M5 dialogue ended with active operations.");

        var missingProject = await AssertThrowsAsync<DialogueProjectException>(
            () => dialogue.StartAsync(new DialogueStartRequest("missing", "Start")));
        Assert(missingProject.ProjectId == "missing",
            "Dialogue project failure did not retain its logical project ID.");
        Assert(dialogue.State == DialogueSessionState.Faulted,
            "Dialogue project load failure did not enter Faulted.");
        Assert(!ownedRunner.IsDialogueRunning && scheduler.ActiveOperations.Count == 0,
            "Dialogue project load failure leaked Runner or Operation work.");

        var lineCountBeforeStop = presenter.Lines.Count;
        await dialogue.StartAsync();
        var stoppedCompletion = dialogue.Completion;
        await WaitForConditionAsync(() => scheduler.ActiveOperations.Count >= 2, 2);
        await dialogue.StopAsync();
        var stopped = await stoppedCompletion;
        Assert(stopped.Kind == DialogueCompletionKind.Stopped,
            "M6 active stop did not produce a Stopped completion.");
        Assert(dialogue.State == DialogueSessionState.Idle,
            "M6 active stop did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(new[]
            {
                "started",
                "completed",
                "started",
                "stopped",
            }),
            "M6 stopped dialogue session events were out of order.");
        Assert(presenter.Lines.Count == lineCountBeforeStop,
            "M6 stop allowed a dialogue-owned Presenter line to escape.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "M6 stop left dialogue-owned operations active.");

        var heroNode = manager.Get<ICharacterActor>("hero").SceneNode;
        Assert(resources.LoadedResourceCount >= 3,
            "Convention-based Actor and Yarn resources did not enter the Runtime cache.");
        await bootstrap.RequestShutdownAsync();
        portraitHost.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(caseService.ShutdownCount == 1,
            "M5 custom service was not shut down exactly once.");
        Assert(caseService.RuntimeWasCancelledAtShutdown,
            "M5 custom service did not observe Runtime cancellation during shutdown.");
        Assert(!IsInstanceValid(heroNode),
            "M5 custom Actor node survived Runtime shutdown.");
        Assert(!IsInstanceValid(bootstrap), "M5 Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(ownedRunner), "M5 DialogueRunner leaked after cleanup.");
        Assert(!IsInstanceValid(portraitHost), "M5 custom Actor host leaked after cleanup.");
        Assert(resources.LoadedResourceCount == 0,
            "Runtime resource cache survived shutdown.");
    }

    private async Task ValidateYarnCommandAdapterAsync()
    {
        var presenter = new HeadlessDialoguePresenter { Name = "ProbePresenter" };
        AddChild(presenter);

        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "M4Commands";
        configuration.Presentation!.Enabled = false;
        configuration.Actors!.LeftPosition = new Vector2(100, 200);
        configuration.Actors.CenterPosition = new Vector2(200, 200);
        configuration.Actors.RightPosition = new Vector2(300, 200);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            CharacterPositionPreset.Center,
            ("red", "res://tests/headless/fixtures/character_red.svg"),
            ("blue", "res://tests/headless/fixtures/character_blue.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "backdrop",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg")));

        var customCommandRan = false;
        var customCommandService = new HeadlessLifecycleService();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "M4Runtime",
            Configuration = configuration,
            ConfigureServices = services => services.AddSingleton<HeadlessLifecycleService>(
                _ => customCommandService),
            ConfigureCommands = commands => commands.Register<HeadlessProbeCommandArguments>(
                (arguments, context) =>
                {
                    Assert(arguments.Value == "alpha",
                        "Custom command received incorrect arguments.");
                    Assert(context.Actors is not null,
                        "Custom command did not receive ActorManager context.");
                    Assert(context.Services?.Get<HeadlessLifecycleService>() ==
                        customCommandService,
                        "Custom command could not resolve a game extension service.");
                    customCommandRan = true;
                    return Task.CompletedTask;
                }),
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var yarnBackend = bootstrap.Host!.Services.Get<IYarnDialogueBackend>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var runner = yarnBackend.Runner;

        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var adapter = bootstrap.Host.Services.Get<IYarnCommandAdapter>();
        var joinedAtFinalState = false;
        var waitedAtFinalState = false;
        var cancelKeptCharacterVisible = false;
        var completeAppliedHiddenState = false;
        presenter.LinePresented += line =>
        {
            var character = manager.Get<ICharacterActor>("hero");
            switch (line.Id)
            {
                case "line:m4-after-join":
                    var background = manager.Get<IBackgroundActor>("backdrop");
                    joinedAtFinalState =
                        character.Position.IsEqualApprox(configuration.Actors.LeftPosition) &&
                        background.Appearance == "dark" &&
                        scheduler.ActiveOperations.Count == 0;
                    break;
                case "line:m4-after-wait":
                    waitedAtFinalState =
                        character.Position.IsEqualApprox(configuration.Actors.RightPosition) &&
                        character.Appearance == "blue";
                    break;
                case "line:m4-after-cancel":
                    cancelKeptCharacterVisible = character.Visible && character.Opacity > 0;
                    break;
                case "line:m4-after-complete":
                    completeAppliedHiddenState =
                        !character.Visible && Mathf.IsZeroApprox(character.Opacity);
                    break;
            }
        };

        await dialogue.StartAsync();
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Count >= 2,
            2);
        Assert(presenter.Lines.Count == 0,
            "Yarn advanced past await while grouped animations were active.");
        await dialogue.Completion;

        Assert(presenter.Lines.SequenceEqual(new[]
            {
                "after join",
                "after wait",
                "after cancel",
                "after complete",
            }),
            "Yarn command test lines were delivered out of order.");
        Assert(joinedAtFinalState,
            "Yarn await did not join both non-waiting grouped animations.");
        Assert(waitedAtFinalState,
            "wait=true allowed Yarn to present the next line before the move completed.");
        Assert(cancelKeptCharacterVisible,
            "cancel incorrectly applied the hide operation's final state.");
        Assert(completeAppliedHiddenState,
            "await complete did not apply the hide operation's final state.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Completed Yarn dialogue left active operations.");

        var commandFailureSignal = string.Empty;
        bootstrap.Connect(
            MajoRuntimeBootstrap.SignalName.RuntimeCommandFailed,
            Callable.From<string, string>((command, message) =>
                commandFailureSignal = $"{command}:{message}"));
        var badArgumentsStart = dialogue.StartAsync(
            new DialogueStartRequest(StartNode: "BadArguments"));
        var badArgumentsCompletion = dialogue.Completion;
        await badArgumentsStart;
        await WaitForConditionAsync(() => adapter.LastFailure is not null, 2);
        Assert(adapter.LastFailure!.CommandName == "char",
            "Bad Yarn arguments were attributed to the wrong command.");
        Assert(adapter.LastFailure.Message.Contains(
                "Usage: char <actor>",
                StringComparison.Ordinal),
            "Bad Yarn arguments did not include command usage.");
        Assert(commandFailureSignal.StartsWith("char:", StringComparison.Ordinal),
            "Command failure was not exposed through the Runtime signal.");
        await WaitForConditionAsync(() => !runner.IsDialogueRunning, 2);
        await AssertThrowsAsync<RuntimeCommandException>(() => badArgumentsCompletion);
        Assert(dialogue.State == DialogueSessionState.Faulted,
            "M6 command failure did not fault the dialogue session.");

        var unhandledCommand = string.Empty;
        runner.Connect(
            DialogueRunner.SignalName.onUnhandledCommand,
            Callable.From<string>(command => unhandledCommand = command));
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "UnknownCommand"));
        await WaitForConditionAsync(() => unhandledCommand.Length > 0, 2);
        Assert(unhandledCommand == "majo_unknown detail",
            "Yarn did not preserve the unhandled command diagnostic text.");
        await dialogue.StopAsync();

        var lineCountBeforeStop = presenter.Lines.Count;
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "StopCancellation"));
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Any(
                operation => operation.Id == "stop-cancelled"),
            2);
        var dialogueScopedOperation = scheduler.ActiveOperations.Single(
            operation => operation.Id == "stop-cancelled");
        await dialogue.StopAsync();
        await WaitForConditionAsync(
            () => dialogueScopedOperation.State == OperationState.Cancelled,
            2);
        Assert(presenter.Lines.Count == lineCountBeforeStop,
            "Yarn presented a line after dialogue-scoped command cancellation.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Stopping Yarn dialogue left a dialogue-scoped operation active.");

        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "CustomCommand"));
        await dialogue.Completion;
        Assert(customCommandRan,
            "Public Runtime command registry did not invoke the custom command.");
        Assert(presenter.Lines[^1] == "custom command completed",
            "Yarn did not continue after the custom Runtime command.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(presenter.Lines.Count(line => line == "custom command completed") == 1,
            "A stopped Yarn command continuation escaped into a later session.");
        Assert(!IsInstanceValid(bootstrap), "M4 Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(runner), "M4 DialogueRunner leaked after cleanup.");
    }

    private async Task ValidateActorSystemAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Actors!.LeftPosition = new Vector2(100, 200);
        configuration.Actors.CenterPosition = new Vector2(200, 200);
        configuration.Actors.RightPosition = new Vector2(300, 200);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            CharacterPositionPreset.Left,
            ("red", "res://tests/headless/fixtures/character_red.svg"),
            ("blue", "res://tests/headless/fixtures/character_blue.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "courtroom",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg")));

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "ActorRuntime",
            Configuration = configuration,
        };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var actorRoot = bootstrap.GetNode<CanvasLayer>("MajoActors");
        var backgroundLayer = actorRoot.GetNode<Control>("Backgrounds");
        var characterLayer = actorRoot.GetNode<Node2D>("Characters");
        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var character = manager.Get<ICharacterActor>(
            manager.Create("hero").Id);
        var background = manager.Get<IBackgroundActor>(
            manager.Create("courtroom").Id);

        Assert(character.SceneNode is Sprite2D,
            "Character factory did not create a Sprite2D.");
        Assert(ReferenceEquals(character.SceneNode.GetParent(), characterLayer),
            "Character Sprite2D was created under the wrong layer.");
        Assert(background.SceneNode is Control,
            "Background factory did not create a Control host.");
        Assert(ReferenceEquals(background.SceneNode.GetParent(), backgroundLayer),
            "Background host was created under the wrong layer.");
        Assert(ReferenceEquals(background.CurrentView.GetParent(), background.SceneNode),
            "Background TextureRect was created under the wrong host.");
        Assert(character.View.Texture is not null, "Character texture was not loaded.");
        Assert(background.CurrentView.Texture is not null, "Background texture was not loaded.");
        Assert(character.Position.IsEqualApprox(configuration.Actors.LeftPosition),
            "Character did not use the configured left position preset.");

        var initialTexture = character.View.Texture;
        character.SetAppearance("blue");
        Assert(character.Appearance == "blue", "Immediate appearance state was not updated.");
        Assert(!ReferenceEquals(initialTexture, character.View.Texture),
            "Character appearance did not replace its Texture2D.");
        var missingAppearance = AssertThrows<ResourceNotFoundException>(() =>
            character.SetAppearance("missing"));
        Assert(missingAppearance.Candidates.Any(path => path.StartsWith(
                "res://assets/characters/hero/missing.",
                StringComparison.Ordinal)),
            "Missing conventional appearance did not report resolved candidates.");

        character.SetPosition(configuration.Actors.LeftPosition);
        var firstMove = character.ChangePosition(
            CharacterPositionPreset.Center, 1, "first-move");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var secondMove = character.ChangePosition(
            CharacterPositionPreset.Right, 0.04, "second-move");
        await firstMove.Completion;
        Assert(firstMove.State == OperationState.Completed,
            "Conflicting prior position operation was not force-completed.");
        Assert(character.Position.IsEqualApprox(configuration.Actors.CenterPosition),
            "Prior position operation did not reach its target before the next began.");
        await secondMove.Completion;
        Assert(character.Position.IsEqualApprox(configuration.Actors.RightPosition),
            "Second position operation did not reach its target.");

        character.SetOpacity(0);
        var fade = character.ChangeOpacity(1, 0.08, "character-fade");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(character.Opacity is > 0 and < 1,
            "Character opacity Tween did not expose an intermediate value.");
        Assert(Mathf.IsEqualApprox(character.TargetOpacity, 1),
            "Character target opacity did not preserve the requested final state.");
        await fade.Completion;
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Character opacity Tween did not reach its final value.");

        var interruptedHide = character.Hide(1, "interrupted-hide");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var replacementShow = character.Show(0.04, "replacement-show");
        await interruptedHide.Completion;
        await replacementShow.Completion;
        Assert(character.Visible,
            "Show interrupting Hide left the Character invisible.");
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Show interrupting Hide did not reach full opacity.");

        var oldBackgroundView = background.CurrentView;
        var crossFade = background.CrossFadeAppearance("light", 0.06, "background-crossfade");
        Assert(background.TargetAppearance == "light" && background.Appearance == "dark",
            "Background did not distinguish target and rendered appearance state.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(background.SceneNode.GetChildCount() == 2,
            "Background crossfade did not retain old and new TextureRects concurrently.");
        await crossFade.Completion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(background.Appearance == "light",
            "Background appearance state did not reach the crossfade target.");
        Assert(!IsInstanceValid(oldBackgroundView),
            "Background crossfade did not release the old TextureRect.");
        Assert(background.SceneNode.GetChildCount() == 1,
            "Background crossfade left an extra TextureRect behind.");

        var resetOperation = character.ChangePosition(
            CharacterPositionPreset.Left, 10, "reset-character");
        await bootstrap.ResetRuntimeAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(resetOperation.State == OperationState.Cancelled,
            "Runtime Reset did not cancel the Actor operation.");
        Assert(manager.Actors.Count == 0, "Runtime Reset did not clear ActorManager state.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Runtime Reset left an active Actor operation.");
        Assert(characterLayer.GetChildCount() == 0,
            "Runtime Reset left Character nodes in the scene.");
        Assert(backgroundLayer.GetChildCount() == 0,
            "Runtime Reset left Background nodes in the scene.");

        var closingCharacter = manager.Create("hero");
        var closingBackground = manager.Create("courtroom");
        var closingCharacterNode = ((IGodotActorView)closingCharacter).SceneNode;
        var closingBackgroundNode = ((IGodotActorView)closingBackground).SceneNode;
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(closingCharacterNode),
            "Runtime shutdown left a Character node behind.");
        Assert(!IsInstanceValid(closingBackgroundNode),
            "Runtime shutdown left a Background node behind.");
        Assert(!IsInstanceValid(actorRoot), "Runtime shutdown left the Actor root behind.");
        Assert(!IsInstanceValid(bootstrap), "Actor Runtime node leaked after shutdown.");
    }

    private static ActorDefinitionConfiguration CreateActorDefinition(
        string id,
        string factoryId,
        CharacterPositionPreset initialPosition,
        params (string Id, string Path)[] appearances)
    {
        var definition = new ActorDefinitionConfiguration
        {
            Id = id,
            FactoryId = factoryId,
            DefaultAppearance = appearances[0].Id,
            InitialPosition = initialPosition,
        };
        foreach (var appearance in appearances)
        {
            definition.Appearances.Add(new ActorAppearanceConfiguration
            {
                Id = appearance.Id,
                TexturePath = appearance.Path,
            });
        }

        return definition;
    }

    private async Task ValidateOperationKernelAsync()
    {
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "OperationRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
            ConfigureServices = services =>
                services.AddSingleton<ShutdownStartsOperationService>(
                    resolver => new ShutdownStartsOperationService(
                        resolver.Get<IOperationScheduler>()),
                    typeof(IOperationScheduler)),
        };
        var tweenTarget = new Node2D { Name = "TweenTarget" };
        AddChild(tweenTarget);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var scheduler = bootstrap.Host!.Services.Get<IOperationScheduler>();
        var shutdownStarter = bootstrap.Host.Services.Get<ShutdownStartsOperationService>();
        var advancedFrames = 0;
        var frameOperation = scheduler.Start(new OperationDefinition(
            async context =>
            {
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
                await context.FrameClock.NextFrameAsync(context.CancellationToken);
                advancedFrames++;
            },
            id: "real-frames"));

        Assert(!frameOperation.Completion.IsCompleted,
            "Real-frame operation completed before Godot advanced a frame.");
        await frameOperation.Completion;
        Assert(advancedFrames == 2, "Operation did not advance across two real Godot frames.");

        tweenTarget.Position = Vector2.Zero;
        var naturalTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(80, 20),
            0.03,
            id: "natural-tween"));
        await naturalTween.Completion;
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(80, 20)),
            "Naturally completed Tween did not reach its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var cancelledTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(100, 0),
            1,
            id: "cancelled-tween"));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await scheduler.CancelAsync(OperationTarget.ForId("cancelled-tween"));
        Assert(cancelledTween.State == OperationState.Cancelled,
            "Cancelled Tween did not enter Cancelled state.");
        Assert(!tweenTarget.Position.IsEqualApprox(new Vector2(100, 0)),
            "Cancelled Tween incorrectly applied its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var completedTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(120, 40),
            1,
            id: "completed-tween"));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await scheduler.CompleteAsync(OperationTarget.ForId("completed-tween"));
        Assert(completedTween.State == OperationState.Completed,
            "Force-completed Tween did not enter Completed state.");
        Assert(tweenTarget.Position.IsEqualApprox(new Vector2(120, 40)),
            "Force-completed Tween did not apply its final property value.");

        tweenTarget.Position = Vector2.Zero;
        var shutdownTween = scheduler.Start(GodotTweenOperation.CreatePropertyTween(
            this,
            tweenTarget,
            new NodePath("position"),
            new Vector2(200, 0),
            10,
            id: "shutdown-tween"));
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Count == 0,
            2);

        Assert(shutdownTween.State == OperationState.Cancelled,
            "Operation active during Runtime exit was not cancelled.");
        Assert(shutdownStarter.StartedOperation.State == OperationState.Cancelled,
            "Operation started during Runtime exit was not cancelled.");
        Assert(!tweenTarget.Position.IsEqualApprox(new Vector2(200, 0)),
            "Runtime-exit cancellation incorrectly completed the Tween.");

        tweenTarget.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap), "Operation Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(tweenTarget), "Tween test node leaked after cleanup.");
    }

    private async Task ValidateSuccessfulRuntimeLifecycleAsync()
    {
        var service = new HeadlessLifecycleService();
        var shutdownSignalCount = 0;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "SuccessfulRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
            ConfigureServices = services =>
                services.AddSingleton<HeadlessLifecycleService>(_ => service),
        };
        bootstrap.RuntimeShutdown += () => shutdownSignalCount++;

        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        Assert(bootstrap.IsInitialized, "Bootstrap did not initialize after entering the scene tree.");
        Assert(bootstrap.DependencyStatus.IsCompatible, bootstrap.DependencyStatus.Message);
        Assert(bootstrap.Host is not null, "Bootstrap did not expose its Runtime Host.");
        Assert(
            ReferenceEquals(service, bootstrap.Host.Services.Get<HeadlessLifecycleService>()),
            "Runtime Host resolved a different service instance.");
        Assert(service.InitializeCount == 1, "Runtime service was not initialized exactly once.");

        var firstSession = bootstrap.Host.SessionCancellationToken;
        await bootstrap.ResetRuntimeAsync();
        Assert(firstSession.IsCancellationRequested, "Reset did not cancel the previous session.");
        Assert(service.ResetCount == 1, "Runtime service was not reset exactly once.");

        var exited = false;
        bootstrap.TreeExited += () => exited = true;
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(exited, "Bootstrap did not exit the scene tree during shutdown.");
        Assert(!IsInstanceValid(bootstrap), "Bootstrap node leaked after shutdown.");
        Assert(service.ShutdownCount == 1, "Runtime service was not shut down exactly once.");
        Assert(shutdownSignalCount == 1,
            "Runtime shutdown signal was not emitted exactly once during tree exit.");

        await bootstrap.ShutdownCompletion;
        Assert(service.ShutdownCount == 1, "Repeated shutdown completion changed service state.");
        Assert(shutdownSignalCount == 1,
            "Repeated shutdown completion emitted another Runtime shutdown signal.");
    }

    private async Task ValidateLifecycleTimeoutAsync()
    {
        var service = new NonCooperativeInitializationService();
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Runtime!.LifecycleTimeoutSeconds = 0.05;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "TimedOutRuntime",
            Configuration = configuration,
            ConfigureServices = services =>
                services.AddSingleton<NonCooperativeInitializationService>(_ => service),
        };
        var exited = false;
        bootstrap.TreeExited += () => exited = true;

        AddChild(bootstrap);
        Exception initializationFailure = null;
        try
        {
            await bootstrap.InitializationCompletion;
        }
        catch (Exception exception)
        {
            initializationFailure = exception;
        }
        finally
        {
            service.CompleteInitialization();
        }

        Assert(initializationFailure is TimeoutException,
            "A service that ignored cancellation did not trigger the hard lifecycle timeout.");
        Assert(initializationFailure.Message.Contains("initialization", StringComparison.Ordinal),
            "Lifecycle timeout did not identify the timed-out operation.");

        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(service.ShutdownCount == 1,
            "A late-completing initialization was not subsequently shut down.");
        Assert(exited && !IsInstanceValid(bootstrap),
            "Timed-out Runtime bootstrap leaked after late initialization cleanup.");
    }

    private async Task ValidateFailedConfigurationCleanupAsync()
    {
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "InvalidRuntime",
            Configuration = new MajoProjectConfiguration(),
        };
        var failureMessage = string.Empty;
        var exited = false;
        bootstrap.RuntimeInitializationFailed += message => failureMessage = message;
        bootstrap.TreeExited += () => exited = true;

        AddChild(bootstrap);
        try
        {
            await bootstrap.InitializationCompletion;
            throw new InvalidOperationException("Invalid configuration unexpectedly initialized.");
        }
        catch (MajoConfigurationException)
        {
        }

        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(failureMessage.Contains("Runtime", StringComparison.Ordinal),
            "Configuration failure did not include a diagnostic path.");
        Assert(bootstrap.Host is null, "Invalid configuration created a Runtime Host.");
        Assert(exited, "Invalid bootstrap did not exit the scene tree.");
        Assert(!IsInstanceValid(bootstrap), "Invalid bootstrap node leaked after failure.");
    }

    private static void ValidateConfigurationDiagnostics()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Actors!.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "hero",
            DefaultAppearance = "default",
            Appearances =
            {
                new ActorAppearanceConfiguration
                {
                    Id = "default",
                    TexturePath = "res://missing/hero-a.png",
                },
            },
        });
        configuration.Actors.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "hero",
            DefaultAppearance = "default",
            Appearances =
            {
                new ActorAppearanceConfiguration
                {
                    Id = "default",
                    TexturePath = "res://missing/hero-b.png",
                },
            },
        });
        configuration.Dialogue!.RequiredYarnRuntimeVersion = "99.0.0";
        configuration.Presentation!.FontSize = 0;
        configuration.Presentation.LettersPerSecond = 0;
        configuration.Presentation.FadeUpDurationSeconds = double.NaN;
        configuration.Audio!.MasterVolume = 2;
        configuration.Audio.DefaultFadeDurationSeconds = double.NaN;
        configuration.Audio.VoiceLocale = string.Empty;
        configuration.Audio.BgmPath = "..";

        var validation = configuration.Validate(_ => false);

        Assert(!validation.IsValid, "Invalid actor configuration passed validation.");
        Assert(validation.Issues.Any(issue =>
                issue.Message.Contains("duplicated", StringComparison.Ordinal)),
            "Duplicate actor ID was not diagnosed.");
        Assert(validation.Issues.Count(issue =>
                issue.Message.Contains("does not exist", StringComparison.Ordinal)) == 2,
            "Invalid actor resource paths were not diagnosed.");
        Assert(validation.Issues.Any(issue =>
                issue.Path.Contains("RequiredYarnRuntimeVersion", StringComparison.Ordinal)),
            "Invalid Yarn runtime version was not diagnosed.");
        Assert(validation.Issues.Count(issue =>
                issue.Path.StartsWith("Presentation", StringComparison.Ordinal)) == 3,
            "Invalid M8 Presentation values were not diagnosed together.");
        Assert(validation.Issues.Count(issue =>
                issue.Path.StartsWith("Audio", StringComparison.Ordinal)) == 4,
            "Invalid M9 Audio values were not diagnosed together.");

        var customActorConfiguration = MajoProjectConfiguration.CreateDefault();
        customActorConfiguration.Actors!.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "custom",
            FactoryId = "custom-payload",
            FactoryConfiguration = new Resource(),
        });
        Assert(customActorConfiguration.Validate(_ => false).IsValid,
            "Factory-owned Actor configuration was incorrectly validated as a texture Actor.");
    }

    private async Task FinalizeEngineObjectsAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task RunWithTimeoutAsync(Task suite, double timeoutSeconds)
    {
        var startedAtMilliseconds = Time.GetTicksMsec();
        var timeoutMilliseconds = timeoutSeconds * 1_000;

        while (!suite.IsCompleted)
        {
            var elapsedMilliseconds = Time.GetTicksMsec() - startedAtMilliseconds;
            if (elapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException(
                    $"Headless test suite exceeded {timeoutSeconds:0.###} seconds.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await suite;
    }

    private async Task WaitForConditionAsync(Func<bool> condition, double timeoutSeconds)
    {
        var startedAtMilliseconds = Time.GetTicksMsec();
        var timeoutMilliseconds = timeoutSeconds * 1_000;
        while (!condition())
        {
            if (Time.GetTicksMsec() - startedAtMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException(
                    $"Condition was not reached within {timeoutSeconds:0.###} seconds.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    [RuntimeCommand(
        "majo_probe",
        Category = "Test",
        Usage = "majo_probe <value>")]
    private sealed class HeadlessProbeCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class HeadlessLifecycleService : IRuntimeService
    {
        public int InitializeCount { get; private set; }

        public int ResetCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            ShutdownCount++;
        }
    }

    private sealed class BlockingBridgePresenter : IDialoguePresenter
    {
        public List<DialogueLine> Lines { get; } = new();

        public async Task PresentLineAsync(
            DialogueLine line,
            DialoguePresentationContext context)
        {
            Lines.Add(line);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.NextLineToken);
            }
            catch (OperationCanceledException)
                when (context.NextLineToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class HeadlessAudioClipLoader : IAudioClipLoader
    {
        private readonly Dictionary<string, IAudioClip> clips =
            new(StringComparer.Ordinal);

        public string PathPrefix => "headless/audio";

        public HeadlessAudioClipLoader Register(string clipId)
        {
            clips.Add(clipId, new GodotAudioClip(
                clipId,
                new AudioStreamGenerator
                {
                    MixRate = 22_050,
                    BufferLength = 0.5f,
                }));
            return this;
        }

        public bool Exists(string clipId, string overridePath = null) =>
            clips.ContainsKey(overridePath ?? clipId);

        public Task<IAudioClip> LoadAsync(
            string clipId,
            string overridePath = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return clips.TryGetValue(overridePath ?? clipId, out var clip)
                ? Task.FromResult(clip)
                : Task.FromException<IAudioClip>(
                    new AudioException($"Headless audio clip '{clipId}' is missing."));
        }
    }

    private sealed class M8MarkupProbe : IDialogueMarkupHandler
    {
        private readonly string id;
        private readonly IList<string> order;

        public M8MarkupProbe(string id, IList<string> order)
        {
            this.id = id;
            this.order = order;
        }

        public List<DialogueText> PreparedTexts { get; } = new();

        public bool SawCancelledCharacterToken { get; private set; }

        public void OnLinePrepared(DialogueText text)
        {
            PreparedTexts.Add(text);
            order.Add($"{id}:prepared");
        }

        public void OnLineDisplayBegan(DialogueText text) =>
            order.Add($"{id}:began");

        public Task OnCharacterWillAppearAsync(
            int characterIndex,
            DialogueText text,
            CancellationToken cancellationToken)
        {
            SawCancelledCharacterToken |= cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }

        public void OnLineDisplayCompleted() => order.Add($"{id}:completed");

        public void OnLineWillDismiss() => order.Add($"{id}:dismissed");
    }

    private sealed class NonCooperativeInitializationService : IRuntimeService
    {
        private readonly TaskCompletionSource initialization =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ShutdownCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => initialization.Task;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public void CompleteInitialization() => initialization.TrySetResult();
    }

    private sealed class ShutdownStartsOperationService : IRuntimeService
    {
        private readonly IOperationScheduler scheduler;

        public ShutdownStartsOperationService(IOperationScheduler scheduler)
        {
            this.scheduler = scheduler;
        }

        public IOperationHandle StartedOperation { get; private set; } = null!;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            StartedOperation = scheduler.Start(new OperationDefinition(
                context => context.FrameClock.NextFrameAsync(context.CancellationToken),
                id: "started-during-shutdown"));
            try
            {
                await StartedOperation.Completion;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

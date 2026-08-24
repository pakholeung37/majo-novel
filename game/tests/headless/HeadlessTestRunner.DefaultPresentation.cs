using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Input;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
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
        Assert(presentationRoot.GetParent() is CanvasLayer { Layer: 20 },
            "Default Dialogue Panel was not placed above Actor CanvasLayers.");
        var lineContent = presentationRoot.FindChild(
            "Margin",
            recursive: true,
            owned: false) as CanvasItem;
        var characterNamePanel = presentationRoot.FindChild(
            "CharacterNamePanel",
            recursive: true,
            owned: false) as CanvasItem;
        var optionContainer = presentationRoot.FindChild(
            "OptionContainer",
            recursive: true,
            owned: false) as CanvasItem;
        Assert(lineContent?.ZIndex == 1 &&
                characterNamePanel?.ZIndex == 2 &&
                characterNamePanel?.GetParent().Name == "LinePresenter" &&
                optionContainer?.ZIndex == 1,
            "Default Presenter content was not layered above its panel backgrounds.");
        var nativeLinePresenter = presentationRoot!.FindChild(
            "LinePresenter",
            recursive: true,
            owned: false) as LinePresenter;
        var nativeLineText = presentationRoot.FindChild(
            "LineText",
            recursive: true,
            owned: false) as RichTextLabel;
        Assert(nativeLinePresenter?.Typewriter is LetterTypewriter &&
                nativeLineText?.BbcodeEnabled == true &&
                nativeLinePresenter.presenterControl?.MouseFilter ==
                    Control.MouseFilterEnum.Ignore,
            "M8 did not reuse Yarn's native letter Typewriter or allow panel clicks " +
            "to reach Runtime input.");
        Assert(backend.Runner.dialoguePresenters is not null &&
                backend.Runner.dialoguePresenters.OfType<LineAdvancer>().Count() == 0,
            "M8 registered Yarn LineAdvancer and bypassed M7 input ownership.");

        await dialogue.StartAsync();
        await WaitForConditionAsync(
            () => presentation.IsLineActive && !presentation.IsLineDisplayComplete,
            2);
        Assert(dialogue.CurrentLineId == "line:m8-first",
            "M8 did not publish the first line through the M6 facade.");
        await WaitForConditionAsync(
            () => nativeLineText!.Text.Contains(
                "[b]A deliberately long first line[/b]",
                StringComparison.Ordinal),
            2);
        Assert(nativeLineText.GetParsedText().Contains(
                "A deliberately long first line for the native typewriter.",
                StringComparison.Ordinal) &&
                !nativeLineText.GetParsedText().Contains("[b]", StringComparison.Ordinal),
            "M8 did not project native Yarn style markup into RichTextLabel BBCode.");

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

        await InjectMouseClickAsync(
            nativeLinePresenter.presenterControl!.GetGlobalRect().GetCenter());
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
        var optionButtons = visibleOptionItems
            .Select(option => option.FindChild(
                "Button",
                recursive: true,
                owned: false) as BaseButton)
            .ToArray();
        Assert(visibleOptionItems.Length == 2 && Enumerable.Range(0, 2).All(index =>
            optionButtons[index] is not null && optionButtons[index]!.IsConnected(
                BaseButton.SignalName.Pressed,
                Callable.From(visibleOptionItems[index].InvokeOptionSelected))),
            "M8 option buttons were not connected to their native selection path.");
        await InjectMouseClickAsync(Vector2.Zero);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(dialogue.IsPresentingOptions && presentation.IsPresentingOptions,
            "M8 treated an unrelated left click as confirmation of the focused option.");
        optionButtons[1]!.EmitSignal(BaseButton.SignalName.Pressed);
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
        Assert(input.ActiveHandleCount == 2,
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
                !presentation.IsLineActive && input.ActiveHandleCount == 2,
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
                !presentation.IsPresentingOptions && input.ActiveHandleCount == 2,
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

        async Task InjectMouseClickAsync(Vector2 position)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Godot.Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = position,
                GlobalPosition = position,
                Pressed = true,
            });
            Godot.Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Position = position,
                GlobalPosition = position,
                Pressed = false,
            });
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
}

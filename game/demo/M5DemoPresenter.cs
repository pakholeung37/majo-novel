#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Demo;

public partial class M5DemoPresenter : CanvasLayer, DialoguePresenterBase
{
    private readonly List<Button> optionButtons = new();
    private Label lineLabel = null!;
    private Label statusLabel = null!;
    private Button continueButton = null!;
    private VBoxContainer optionList = null!;
    private TaskCompletionSource? lineCompletion;

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public override void _Ready()
    {
        Layer = 20;
        BuildInterface();
    }

    public YarnTask OnDialogueStartedAsync()
    {
        SetStatus("Runtime ready · Yarn dialogue running");
        return YarnTask.CompletedTask;
    }

    public async YarnTask RunLineAsync(
        LocalizedLine line,
        LineCancellationToken token)
    {
        ClearOptions();
        lineLabel.Text = line.RawText ?? string.Empty;
        continueButton.Visible = true;
        lineCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.NextLineToken.Register(
            () => lineCompletion.TrySetResult());
        await lineCompletion.Task;
        lineCompletion = null;
        continueButton.Visible = false;
    }

    public async YarnTask<DialogueOption?> RunOptionsAsync(
        DialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        continueButton.Visible = false;
        ClearOptions();
        var completion = new TaskCompletionSource<DialogueOption?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => completion.TrySetResult(null));

        foreach (var option in dialogueOptions.Where(option => option.IsAvailable))
        {
            var button = new Button
            {
                Text = option.Line.RawText ?? option.TextID,
                CustomMinimumSize = new Vector2(0, 52),
            };
            button.Pressed += () => completion.TrySetResult(option);
            optionList.AddChild(button);
            optionButtons.Add(button);
        }

        if (optionButtons.Count == 0)
        {
            return await DialogueRunner.NoOptionSelected;
        }

        optionButtons[0].GrabFocus();
        var selected = await completion.Task;
        ClearOptions();
        return selected;
    }

    public YarnTask OnDialogueCompleteAsync()
    {
        continueButton.Visible = false;
        ClearOptions();
        return YarnTask.CompletedTask;
    }

    public void SetStatus(string text)
    {
        if (GodotObject.IsInstanceValid(statusLabel))
        {
            statusLabel.Text = text;
        }
    }

    private void BuildInterface()
    {
        var shade = new ColorRect
        {
            Color = new Color("10131acc"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        statusLabel = new Label
        {
            Text = "Starting Majo Runtime...",
            Position = new Vector2(28, 24),
        };
        statusLabel.AddThemeFontSizeOverride("font_size", 18);
        AddChild(statusLabel);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.08f,
            AnchorTop = 0.67f,
            AnchorRight = 0.92f,
            AnchorBottom = 0.95f,
        };
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 32);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_right", 32);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);

        lineLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        lineLabel.AddThemeFontSizeOverride("font_size", 28);
        content.AddChild(lineLabel);

        optionList = new VBoxContainer();
        optionList.AddThemeConstantOverride("separation", 8);
        content.AddChild(optionList);

        continueButton = new Button
        {
            Text = "继续  ▶",
            CustomMinimumSize = new Vector2(180, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            Visible = false,
        };
        continueButton.Pressed += () => lineCompletion?.TrySetResult();
        content.AddChild(continueButton);
    }

    private void ClearOptions()
    {
        foreach (var button in optionButtons)
        {
            button.QueueFree();
        }

        optionButtons.Clear();
    }
}

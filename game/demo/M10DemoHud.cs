#nullable enable

using System;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.History;

namespace Majo.Demo;

public partial class M10DemoHud : CanvasLayer
{
    private IAutoManager? auto;
    private IHistoryManager? history;
    private Label status = null!;
    private Button autoButton = null!;
    private Button historyButton = null!;

    public override void _Ready()
    {
        Layer = 30;
        BuildInterface();
        Refresh();
    }

    public override void _ExitTree() => Unbind();

    public void Bind(IAutoManager autoManager, IHistoryManager historyManager)
    {
        ArgumentNullException.ThrowIfNull(autoManager);
        ArgumentNullException.ThrowIfNull(historyManager);
        Unbind();
        auto = autoManager;
        history = historyManager;
        auto.ModeChanged += HandleModeChanged;
        auto.PauseChanged += HandlePauseChanged;
        history.OpenStateChanged += HandleHistoryChanged;
        Refresh();
    }

    public void SetStatus(string text)
    {
        if (GodotObject.IsInstanceValid(status))
        {
            status.Text = text;
        }
    }

    private void BuildInterface()
    {
        var panel = new PanelContainer
        {
            AnchorLeft = 0.68f,
            AnchorTop = 0.025f,
            AnchorRight = 0.98f,
            AnchorBottom = 0.15f,
        };
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        panel.AddChild(margin);

        var layout = new VBoxContainer();
        margin.AddChild(layout);
        status = new Label { Text = "Majo Runtime · Manual" };
        layout.AddChild(status);

        var actions = new HBoxContainer();
        layout.AddChild(actions);
        autoButton = new Button
        {
            Text = "Auto: Off (A)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        autoButton.Pressed += () => auto?.Toggle();
        actions.AddChild(autoButton);
        historyButton = new Button
        {
            Text = "History (H)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        historyButton.Pressed += () => history?.Toggle();
        actions.AddChild(historyButton);
    }

    private void Unbind()
    {
        if (auto is not null)
        {
            auto.ModeChanged -= HandleModeChanged;
            auto.PauseChanged -= HandlePauseChanged;
        }

        if (history is not null)
        {
            history.OpenStateChanged -= HandleHistoryChanged;
        }

        auto = null;
        history = null;
    }

    private void HandleModeChanged(AutoModeChanged _) => Refresh();

    private void HandlePauseChanged(AutoPauseChanged _) => Refresh();

    private void HandleHistoryChanged(HistoryOpenStateChanged _) => Refresh();

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(autoButton) ||
            !GodotObject.IsInstanceValid(historyButton))
        {
            return;
        }

        var isAuto = auto?.Mode == AutoMode.Auto;
        autoButton.Text = isAuto ? "Auto: On (A)" : "Auto: Off (A)";
        historyButton.Text = history?.IsOpen == true ? "Close History (H)" : "History (H)";
        if (GodotObject.IsInstanceValid(status))
        {
            status.Text = isAuto
                ? auto?.IsPaused == true
                    ? "Majo Runtime · Auto paused"
                    : "Majo Runtime · Auto reading"
                : "Majo Runtime · Manual · Space to advance";
        }
    }
}

#nullable enable

using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;
using Majo.Runtime.State;

namespace Majo.Demo;

public partial class RuntimeDemoHud : CanvasLayer
{
    private IAutoManager? auto;
    private IHistoryManager? history;
    private IStateManager? state;
    private IScriptPlayback? script;
    private IDialoguePlayer? dialogue;
    private IOperationScheduler? operations;
    private Label status = null!;
    private Label playback = null!;
    private Label feedback = null!;
    private Button autoButton = null!;
    private Button historyButton = null!;
    private Button captureButton = null!;
    private Button restoreButton = null!;

    public override void _Ready()
    {
        Layer = 30;
        BuildInterface();
        Refresh();
    }

    public override void _ExitTree() => Unbind();

    public void Bind(
        IAutoManager autoManager,
        IHistoryManager historyManager,
        IStateManager stateManager,
        IScriptPlayback scriptPlayback,
        IDialoguePlayer dialoguePlayer,
        IOperationScheduler operationScheduler)
    {
        ArgumentNullException.ThrowIfNull(autoManager);
        ArgumentNullException.ThrowIfNull(historyManager);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(scriptPlayback);
        ArgumentNullException.ThrowIfNull(dialoguePlayer);
        ArgumentNullException.ThrowIfNull(operationScheduler);
        Unbind();
        auto = autoManager;
        history = historyManager;
        state = stateManager;
        script = scriptPlayback;
        dialogue = dialoguePlayer;
        operations = operationScheduler;
        auto.ModeChanged += HandleModeChanged;
        auto.PauseChanged += HandlePauseChanged;
        history.OpenStateChanged += HandleHistoryChanged;
        state.StatusChanged += HandleStateStatusChanged;
        state.Captured += HandleCaptured;
        state.Restored += HandleRestored;
        state.Failed += HandleStateFailed;
        script.PhaseChanged += HandlePhaseChanged;
        dialogue.LinePresented += HandleLinePresented;
        Refresh();
    }

    public void SetStatus(string text)
    {
        if (GodotObject.IsInstanceValid(feedback))
        {
            feedback.Text = text;
        }
    }

    private void BuildInterface()
    {
        var panel = new PanelContainer
        {
            AnchorLeft = 0.62f,
            AnchorTop = 0.025f,
            AnchorRight = 0.98f,
            AnchorBottom = 0.245f,
            MouseFilter = Control.MouseFilterEnum.Stop,
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
        status = new Label { Text = "Runtime Validation Demo · Manual" };
        status.AddThemeFontSizeOverride("font_size", 18);
        layout.AddChild(status);
        playback = new Label { Text = "Script: Idle" };
        layout.AddChild(playback);
        feedback = new Label
        {
            Text = "Capture is available when a line has finished displaying.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        layout.AddChild(feedback);

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

        var stateActions = new HBoxContainer();
        layout.AddChild(stateActions);
        captureButton = new Button
        {
            Name = "CaptureStateButton",
            Text = "Capture State",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        captureButton.Pressed += () => _ = CaptureStateAsync();
        stateActions.AddChild(captureButton);
        restoreButton = new Button
        {
            Name = "RestoreStateButton",
            Text = "Restore State",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        restoreButton.Pressed += () => _ = RestoreStateAsync();
        stateActions.AddChild(restoreButton);
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

        if (state is not null)
        {
            state.StatusChanged -= HandleStateStatusChanged;
            state.Captured -= HandleCaptured;
            state.Restored -= HandleRestored;
            state.Failed -= HandleStateFailed;
        }

        if (script is not null)
        {
            script.PhaseChanged -= HandlePhaseChanged;
        }

        if (dialogue is not null)
        {
            dialogue.LinePresented -= HandleLinePresented;
        }

        auto = null;
        history = null;
        state = null;
        script = null;
        dialogue = null;
        operations = null;
    }

    private void HandleModeChanged(AutoModeChanged _) => Refresh();

    private void HandlePauseChanged(AutoPauseChanged _) => Refresh();

    private void HandleHistoryChanged(HistoryOpenStateChanged _) => Refresh();

    private void HandleStateStatusChanged(StateStatusChanged _) => Refresh();

    private void HandleCaptured(StateSnapshot snapshot)
    {
        feedback.Text = $"Captured {snapshot.Records.Count} Game records; History stays Session-only.";
        Refresh();
    }

    private void HandleRestored(StateSnapshot _)
    {
        feedback.Text = "Game State restored. History was intentionally preserved.";
        Refresh();
    }

    private void HandleStateFailed(Exception exception)
    {
        feedback.Text = $"State operation failed: {exception.Message}";
        Refresh();
    }

    private void HandlePhaseChanged(ScriptPlaybackPhaseChanged _) => Refresh();

    private void HandleLinePresented(DialogueLine _) => Refresh();

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(autoButton) ||
            !GodotObject.IsInstanceValid(historyButton) ||
            !GodotObject.IsInstanceValid(captureButton) ||
            !GodotObject.IsInstanceValid(restoreButton))
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
                    ? "Runtime Validation Demo · Auto paused"
                    : "Runtime Validation Demo · Auto reading"
                : "Runtime Validation Demo · Manual · Space to advance";
        }

        var spot = script?.CurrentSpot;
        playback.Text = $"Script: {script?.Phase ?? ScriptPlaybackPhase.Idle} · " +
            $"Line: {spot?.LineId ?? dialogue?.CurrentLineId ?? "<none>"} · " +
            $"Operations: {operations?.ActiveOperations.Count ?? 0}";
        var stateIdle = state?.Status == StateManagerStatus.Idle;
        captureButton.Disabled = stateIdle != true || script?.CanCaptureCheckpoint != true;
        restoreButton.Disabled = stateIdle != true || state?.CurrentSnapshot is null;
    }

    private async Task CaptureStateAsync()
    {
        if (state is null || script?.CanCaptureCheckpoint != true)
        {
            return;
        }

        try
        {
            feedback.Text = "Capturing Game State...";
            Refresh();
            await state.CaptureGameAsync();
        }
        catch (Exception exception)
        {
            feedback.Text = $"Capture failed: {exception.Message}";
        }
        finally
        {
            Refresh();
        }
    }

    private async Task RestoreStateAsync()
    {
        var snapshot = state?.CurrentSnapshot;
        if (state is null || snapshot is null)
        {
            return;
        }

        try
        {
            feedback.Text = "Restoring Game State...";
            Refresh();
            await state.RestoreGameAsync(snapshot);
        }
        catch (Exception exception)
        {
            feedback.Text = $"Restore failed: {exception.Message}";
        }
        finally
        {
            Refresh();
        }
    }
}

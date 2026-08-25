#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.Diagnostics;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

namespace Majo.Demo;

/// <summary>
/// An explicit M11-M15 validation surface built only from public Runtime contracts.
/// </summary>
public partial class RuntimeDemoHud : CanvasLayer
{
    private IPlaybackModeManager? playbackManager;
    private IHistoryManager? history;
    private IStateManager? state;
    private IScriptPlayback? script;
    private IDialoguePlayer? dialogue;
    private IOperationScheduler? operations;
    private IInputManager? input;
    private IFrameClock? frameClock;
    private IRuntimeDiagnostics? diagnostics;
    private IStateEligibility? eligibility;
    private IGameSessionCoordinator? sessions;
    private ISaveSlotManager? slots;
    private ISaveProductManager? saveProduct;
    private ISettingsManager? settings;
    private IGlobalStateManager? globalState;
    private IPlayedRegister? played;
    private ISaveLoadOverlay? saveLoadOverlay;
    private ISettingsOverlay? settingsOverlay;
    private Func<string>? sessionId;
    private DefaultDebugOverlay? debugOverlay;

    private Label status = null!;
    private Label runtimeState = null!;
    private Label eligibilityState = null!;
    private Label persistenceState = null!;
    private Label feedback = null!;
    private Button autoButton = null!;
    private Button skipReadButton = null!;
    private Button skipAllButton = null!;
    private Button historyButton = null!;
    private Button captureButton = null!;
    private Button restoreButton = null!;
    private Button quickSaveButton = null!;
    private Button newGameButton = null!;
    private bool isStartingNewGame;

    public override void _Ready()
    {
        Layer = 30;
        BuildInterface();
        Refresh();
    }

    public override void _ExitTree() => Unbind();

    public void Bind(
        IPlaybackModeManager playbackModeManager,
        IHistoryManager historyManager,
        IStateManager stateManager,
        IScriptPlayback scriptPlayback,
        IDialoguePlayer dialoguePlayer,
        IOperationScheduler operationScheduler,
        IInputManager inputManager,
        IFrameClock runtimeFrameClock,
        IRuntimeDiagnostics runtimeDiagnostics,
        IStateEligibility stateEligibility,
        IGameSessionCoordinator sessionCoordinator,
        ISaveSlotManager saveSlotManager,
        ISaveProductManager saveProductManager,
        ISettingsManager settingsManager,
        IGlobalStateManager globalStateManager,
        IPlayedRegister playedRegister,
        ISaveLoadOverlay saveLoad,
        ISettingsOverlay settingsUi,
        Func<string> currentSessionId,
        DefaultDebugOverlay? debugUi = null)
    {
        ArgumentNullException.ThrowIfNull(playbackModeManager);
        ArgumentNullException.ThrowIfNull(historyManager);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(scriptPlayback);
        ArgumentNullException.ThrowIfNull(dialoguePlayer);
        ArgumentNullException.ThrowIfNull(operationScheduler);
        ArgumentNullException.ThrowIfNull(inputManager);
        ArgumentNullException.ThrowIfNull(runtimeFrameClock);
        ArgumentNullException.ThrowIfNull(runtimeDiagnostics);
        ArgumentNullException.ThrowIfNull(stateEligibility);
        ArgumentNullException.ThrowIfNull(sessionCoordinator);
        ArgumentNullException.ThrowIfNull(saveSlotManager);
        ArgumentNullException.ThrowIfNull(saveProductManager);
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(globalStateManager);
        ArgumentNullException.ThrowIfNull(playedRegister);
        ArgumentNullException.ThrowIfNull(saveLoad);
        ArgumentNullException.ThrowIfNull(settingsUi);
        ArgumentNullException.ThrowIfNull(currentSessionId);

        Unbind();
        playbackManager = playbackModeManager;
        history = historyManager;
        state = stateManager;
        script = scriptPlayback;
        dialogue = dialoguePlayer;
        operations = operationScheduler;
        input = inputManager;
        frameClock = runtimeFrameClock;
        diagnostics = runtimeDiagnostics;
        eligibility = stateEligibility;
        sessions = sessionCoordinator;
        slots = saveSlotManager;
        saveProduct = saveProductManager;
        settings = settingsManager;
        globalState = globalStateManager;
        played = playedRegister;
        saveLoadOverlay = saveLoad;
        settingsOverlay = settingsUi;
        sessionId = currentSessionId;
        debugOverlay = debugUi;

        playbackModeManager.ModeChanged += HandleModeChanged;
        playbackModeManager.PauseChanged += HandlePauseChanged;
        historyManager.OpenStateChanged += HandleHistoryChanged;
        stateManager.StatusChanged += HandleStateStatusChanged;
        stateManager.Captured += HandleCaptured;
        stateManager.Restored += HandleRestored;
        stateManager.Failed += HandleStateFailed;
        scriptPlayback.PhaseChanged += HandlePhaseChanged;
        dialoguePlayer.LinePresented += HandleLinePresented;
        sessionCoordinator.SessionChanged += HandleSessionChanged;
        saveSlotManager.OperationProgressed += HandleSlotProgressed;
        saveSlotManager.OperationCompleted += HandleSlotCompleted;
        saveSlotManager.OperationFailed += HandleSlotFailed;
        saveProductManager.AutoSaveCommitted += HandleAutoSaveCommitted;
        saveProductManager.AutoSaveFailed += HandleAutoSaveFailed;
        settingsManager.Changed += HandleSettingsChanged;
        settingsManager.PersistenceFailed += HandleSettingsFailed;
        globalStateManager.PersistenceFailed += HandleGlobalFailed;
        playedRegister.Changed += HandlePlayedChanged;
        Refresh();
    }

    public void SetStatus(string text)
    {
        if (GodotObject.IsInstanceValid(feedback)) feedback.Text = text;
    }

    private void BuildInterface()
    {
        var panel = new PanelContainer
        {
            AnchorLeft = 0.59f,
            AnchorTop = 0.02f,
            AnchorRight = 0.99f,
            AnchorBottom = 0.67f,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        panel.AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var layout = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(layout);

        status = new Label { Text = "M11-M15 Runtime Validation · Manual" };
        status.AddThemeFontSizeOverride("font_size", 18);
        layout.AddChild(status);
        runtimeState = AddInfoLabel(layout, "Runtime starting...");
        eligibilityState = AddInfoLabel(layout, "Save eligibility: checking...");
        persistenceState = AddInfoLabel(layout, "Persistence: checking...");
        feedback = AddInfoLabel(layout, "Complete a line to reach the safe save boundary.");

        AddSection(layout, "M11 · Diagnostics", row =>
        {
            AddButton(row, "Inspector (F3)", () => debugOverlay?.Toggle());
            AddButton(row, "Export Report", () => _ = ExportReportAsync());
        });
        AddSection(layout, "M12 · Durable State / Session", row =>
        {
            captureButton = AddButton(row, "Capture", () => _ = CaptureStateAsync());
            restoreButton = AddButton(row, "Restore", () => _ = RestoreStateAsync());
            newGameButton = AddButton(row, "New Game", () => _ = NewGameAsync());
        });
        AddSection(layout, "M13 · Save / Load", row =>
        {
            AddButton(row, "Saves (M)", () => saveLoadOverlay?.Toggle());
            quickSaveButton = AddButton(row, "Quick Save", () => _ = QuickSaveAsync());
            AddButton(row, "Arm Auto Save", RequestAutoSave);
        });
        AddSection(layout, "M14 · Settings / Global", row =>
        {
            AddButton(row, "Settings", () => settingsOverlay?.Toggle());
            AddButton(row, "Flush", () => _ = FlushPersistentStateAsync());
            AddButton(row, "Defaults", () => settings?.ResetToDefaults());
        });
        AddSection(layout, "M15 · Played / Playback", row =>
        {
            autoButton = AddButton(row, "Auto", () => playbackManager?.ToggleAuto());
            skipReadButton = AddButton(row, "Skip Read", () => ToggleMode(PlaybackMode.SkipReadOnly));
            skipAllButton = AddButton(row, "Skip All", () => ToggleMode(PlaybackMode.SkipAll));
            AddButton(row, "Clear Played", ClearPlayed);
            historyButton = AddButton(row, "History (H)", () => history?.Toggle());
        });
    }

    private static Label AddInfoLabel(Container parent, string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        parent.AddChild(label);
        return label;
    }

    private static void AddSection(VBoxContainer parent, string title, Action<HBoxContainer> build)
    {
        var label = new Label { Text = title };
        label.AddThemeColorOverride("font_color", new Color("8fd3ff"));
        parent.AddChild(label);
        var row = new HBoxContainer();
        parent.AddChild(row);
        build(row);
    }

    private static Button AddButton(Container parent, string text, Action pressed)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    private void Unbind()
    {
        if (playbackManager is not null)
        {
            playbackManager.ModeChanged -= HandleModeChanged;
            playbackManager.PauseChanged -= HandlePauseChanged;
        }
        if (history is not null) history.OpenStateChanged -= HandleHistoryChanged;
        if (state is not null)
        {
            state.StatusChanged -= HandleStateStatusChanged;
            state.Captured -= HandleCaptured;
            state.Restored -= HandleRestored;
            state.Failed -= HandleStateFailed;
        }
        if (script is not null) script.PhaseChanged -= HandlePhaseChanged;
        if (dialogue is not null) dialogue.LinePresented -= HandleLinePresented;
        if (sessions is not null) sessions.SessionChanged -= HandleSessionChanged;
        if (slots is not null)
        {
            slots.OperationProgressed -= HandleSlotProgressed;
            slots.OperationCompleted -= HandleSlotCompleted;
            slots.OperationFailed -= HandleSlotFailed;
        }
        if (saveProduct is not null)
        {
            saveProduct.AutoSaveCommitted -= HandleAutoSaveCommitted;
            saveProduct.AutoSaveFailed -= HandleAutoSaveFailed;
        }
        if (settings is not null)
        {
            settings.Changed -= HandleSettingsChanged;
            settings.PersistenceFailed -= HandleSettingsFailed;
        }
        if (globalState is not null) globalState.PersistenceFailed -= HandleGlobalFailed;
        if (played is not null) played.Changed -= HandlePlayedChanged;

        playbackManager = null;
        history = null;
        state = null;
        script = null;
        dialogue = null;
        operations = null;
        input = null;
        frameClock = null;
        diagnostics = null;
        eligibility = null;
        sessions = null;
        slots = null;
        saveProduct = null;
        settings = null;
        globalState = null;
        played = null;
        saveLoadOverlay = null;
        settingsOverlay = null;
        sessionId = null;
        debugOverlay = null;
    }

    private void HandleModeChanged(PlaybackModeChanged _) => Refresh();
    private void HandlePauseChanged(PlaybackPauseStateChanged _) => Refresh();
    private void HandleHistoryChanged(HistoryOpenStateChanged _) => Refresh();
    private void HandleStateStatusChanged(StateStatusChanged _) => Refresh();
    private void HandlePhaseChanged(ScriptPlaybackPhaseChanged _) => Refresh();
    private void HandleLinePresented(DialogueLine _) => Refresh();
    private void HandlePlayedChanged() => Refresh();

    private void HandleCaptured(StateSnapshot snapshot)
    {
        SetStatus($"Captured {snapshot.Records.Count} durable Game records.");
        Refresh();
    }

    private void HandleRestored(StateSnapshot _)
    {
        SetStatus("Game State restored; Session History and Global Played state were preserved.");
        Refresh();
    }

    private void HandleStateFailed(Exception exception) => Fail("State", exception);

    private void HandleSessionChanged(GameSessionChanged change)
    {
        SetStatus($"{change.Transition}: Session {ShortId(change.SessionId)}");
        Refresh();
    }

    private void HandleSlotProgressed(SaveSlotOperationInfo operation)
    {
        SetStatus($"{operation.Slot}: {operation.Phase}");
        Refresh();
    }

    private void HandleSlotCompleted(SaveSlotOperationInfo operation)
    {
        SetStatus($"{operation.Slot}: {operation.Status} completed.");
        Refresh();
    }

    private void HandleSlotFailed(SaveSlotOperationInfo operation)
    {
        SetStatus($"{operation.Slot}: {operation.FailureMessage}");
        Refresh();
    }

    private void HandleAutoSaveCommitted(SaveSlotMetadata metadata)
    {
        SetStatus($"Auto Save committed to {metadata.Id}.");
        Refresh();
    }

    private void HandleAutoSaveFailed(Exception exception) => Fail("Auto Save", exception);

    private void HandleSettingsChanged(SettingsChanged change)
    {
        SetStatus($"Settings projected: {change.Current.LettersPerSecond} letters/s, " +
            $"Auto +{change.Current.AutoExtraDelaySeconds:0.##}s.");
        Refresh();
    }

    private void HandleSettingsFailed(Exception exception) => Fail("Settings persistence", exception);
    private void HandleGlobalFailed(Exception exception) => Fail("Global persistence", exception);

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(status) || !GodotObject.IsInstanceValid(captureButton)) return;

        var mode = playbackManager?.Mode ?? PlaybackMode.Manual;
        var paused = playbackManager?.IsPaused == true;
        status.Text = $"M11-M15 Runtime Validation · {mode}" + (paused ? " paused" : string.Empty);
        autoButton.Text = mode == PlaybackMode.Auto ? "Auto: On" : "Auto";
        skipReadButton.Text = mode == PlaybackMode.SkipReadOnly ? "Read: On" : "Skip Read";
        skipAllButton.Text = mode == PlaybackMode.SkipAll ? "All: On" : "Skip All";
        historyButton.Text = history?.IsOpen == true ? "Close History" : "History (H)";

        var spot = script?.CurrentSpot;
        runtimeState.Text =
            $"Session {ShortId(sessionId?.Invoke())} · {script?.Phase ?? ScriptPlaybackPhase.Idle} · " +
            $"{spot?.LineId ?? dialogue?.CurrentLineId ?? "<none>"} · " +
            $"Ops {operations?.ActiveOperations.Count ?? 0} · Logs {diagnostics?.RecentEvents.Count ?? 0}";

        var saveCheck = eligibility?.Check(StateOperationKind.Save);
        eligibilityState.Text = saveCheck?.IsAllowed == true
            ? "Save eligibility: allowed at this AwaitingAdvance boundary."
            : "Save eligibility: " + string.Join(", ",
                saveCheck?.Reasons.Select(reason => reason.Code.ToString()) ?? ["unavailable"]);

        var slotStatus = slots?.CurrentOperation is { } operation
            ? $"{operation.Slot}/{operation.Phase}"
            : slots?.Status.ToString() ?? "Unavailable";
        persistenceState.Text =
            $"Slots {slotStatus} · Settings {(settings?.IsDirty == true ? "dirty" : "saved")}" +
            $"/{settings?.RecoveryStatus.Kind} · Global {(globalState?.IsDirty == true ? "dirty" : "saved")}" +
            $"/{globalState?.RecoveryStatus.Kind} · Played {played?.Entries.Count ?? 0}";

        var idle = state?.Status == StateManagerStatus.Idle;
        captureButton.Disabled = idle != true || saveCheck?.IsAllowed != true;
        restoreButton.Disabled = idle != true || state?.CurrentSnapshot is null;
        quickSaveButton.Disabled = slots?.CanSave(new SaveSlotId(SaveSlotKind.Quick, 1)).IsAllowed != true;
        newGameButton.Disabled = isStartingNewGame;
    }

    private void ToggleMode(PlaybackMode requested)
    {
        if (playbackManager is null) return;
        playbackManager.SetMode(playbackManager.Mode == requested ? PlaybackMode.Manual : requested);
        Refresh();
    }

    private async Task ExportReportAsync()
    {
        if (diagnostics is null) return;
        try
        {
            var path = await diagnostics.ExportReportAsync();
            SetStatus($"Diagnostic report exported: {path}");
        }
        catch (Exception exception) { Fail("Diagnostic export", exception); }
    }

    private async Task CaptureStateAsync()
    {
        if (state is null || input is null || frameClock is null) return;
        using var advanceGuard = BeginAdvanceGuard("demo-capture-advance-guard");
        using var playbackPause = playbackManager?.AcquirePause(PlaybackPauseReason.External);
        try
        {
            SetStatus("Capturing durable Game State...");
            await state.CaptureGameAsync();
        }
        catch (Exception exception) { Fail("Capture", exception); }
        finally
        {
            await DrainInitiatingInputAsync();
            Refresh();
        }
    }

    private async Task RestoreStateAsync()
    {
        var snapshot = state?.CurrentSnapshot;
        if (state is null || snapshot is null || input is null || frameClock is null) return;
        using var advanceGuard = BeginAdvanceGuard("demo-restore-advance-guard");
        playbackManager?.SetMode(PlaybackMode.Manual);
        using var playbackPause = playbackManager?.AcquirePause(PlaybackPauseReason.StateRestore);
        try
        {
            SetStatus("Running restore transaction...");
            await state.RestoreGameAsync(snapshot);
        }
        catch (Exception exception) { Fail("Restore", exception); }
        finally
        {
            await DrainInitiatingInputAsync();
            Refresh();
        }
    }

    private async Task NewGameAsync()
    {
        if (sessions is null || dialogue is null || input is null || frameClock is null ||
            isStartingNewGame)
        {
            return;
        }

        isStartingNewGame = true;
        using var advanceGuard = BeginAdvanceGuard("demo-new-game-advance-guard");
        try
        {
            SetStatus("Starting a clean Game + Session; Global and Settings remain.");
            Refresh();
            await sessions.NewGameAsync();

            // The button callback runs inside the input event that activated it. Cross a
            // frame before registering the new line scope so that click cannot hurry or
            // dismiss the first line of the new session.
            await DrainInitiatingInputAsync();
            await dialogue.StartAsync();
            await DrainInitiatingInputAsync();
        }
        catch (Exception exception) { Fail("New Game", exception); }
        finally
        {
            isStartingNewGame = false;
            Refresh();
        }
    }

    private async Task QuickSaveAsync()
    {
        if (saveProduct is null || input is null || frameClock is null) return;
        using var advanceGuard = BeginAdvanceGuard("demo-quick-save-advance-guard");
        using var playbackPause = playbackManager?.AcquirePause(PlaybackPauseReason.External);
        try
        {
            SetStatus("Writing the next rotating Quick Save...");
            var metadata = await saveProduct.QuickSaveAsync();
            SetStatus($"Quick Save committed to {metadata.Id}.");
        }
        catch (Exception exception) { Fail("Quick Save", exception); }
        finally
        {
            await DrainInitiatingInputAsync();
            Refresh();
        }
    }

    private void RequestAutoSave()
    {
        saveProduct?.RequestAutoSaveCandidate("runtime-demo-hud");
        SetStatus("Auto Save armed; it will commit once the next safe line boundary is reached.");
        Refresh();
    }

    private async Task FlushPersistentStateAsync()
    {
        if (settings is null || globalState is null) return;
        try
        {
            SetStatus("Flushing Settings and Global State...");
            await settings.FlushAsync();
            await globalState.FlushAsync();
            SetStatus("Settings and Global State committed.");
        }
        catch (Exception exception) { Fail("Persistent flush", exception); }
        finally { Refresh(); }
    }

    private void ClearPlayed()
    {
        played?.Clear();
        SetStatus("Played Register cleared; Global State is dirty until flush/debounce.");
        Refresh();
    }

    private void Fail(string operation, Exception exception)
    {
        SetStatus($"{operation} failed: {exception.Message}");
        Refresh();
    }

    private IInputHandle BeginAdvanceGuard(string name) => input!.AcquireMuter(
        this,
        new InputMuteOptions([MajoInputActions.Advance], name));

    private async Task DrainInitiatingInputAsync()
    {
        if (frameClock is not null)
        {
            await frameClock.NextFrameAsync();
            await frameClock.NextFrameAsync();
        }
    }

    private static string ShortId(string? value) => string.IsNullOrWhiteSpace(value)
        ? "<none>"
        : value.Length <= 8 ? value : value[..8];
}

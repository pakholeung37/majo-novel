#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Save;

namespace Majo.Runtime.UI.SaveLoad;

public interface ISaveLoadOverlay : IRuntimeService
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void Toggle();
}

public sealed class DefaultSaveLoadOverlay : ISaveLoadOverlay
{
    private readonly Node owner;
    private readonly ISaveSlotManager slots;
    private readonly IInputManager input;
    private readonly IFrameClock? frameClock;
    private readonly IPlaybackModeManager? playback;
    private readonly PackedScene? scene;
    private readonly CancellationTokenSource lifetime = new();
    private CanvasLayer? layer;
    private Control? root;
    private VBoxContainer? entries;
    private Label? statusLabel;
    private IInputHandle? toggleInput;
    private IInputHandle? modalInput;
    private (SaveSlotId Slot, string Action)? pendingConfirmation;
    private bool isInitialized;

    public DefaultSaveLoadOverlay(
        Node owner,
        ISaveSlotManager slots,
        IInputManager input,
        PackedScene? scene = null,
        IFrameClock? frameClock = null,
        IPlaybackModeManager? playback = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.slots = slots ?? throw new ArgumentNullException(nameof(slots));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.scene = scene;
        this.frameClock = frameClock;
        this.playback = playback;
    }

    public bool IsOpen { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }
        root = BuildView();
        layer = new CanvasLayer { Name = "MajoSaveLoadLayer", Layer = 45 };
        owner.AddChild(layer);
        layer.AddChild(root);
        slots.SlotsChanged += HandleSlotsChanged;
        slots.OperationProgressed += HandleOperationProgressed;
        slots.OperationFailed += HandleOperationFailed;
        EnsureToggleInput();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();
        EnsureToggleInput();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }
        Close();
        slots.SlotsChanged -= HandleSlotsChanged;
        slots.OperationProgressed -= HandleOperationProgressed;
        slots.OperationFailed -= HandleOperationFailed;
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
            toggleInput.Dispose();
            toggleInput = null;
        }
        lifetime.Cancel();
        if (layer is not null && GodotObject.IsInstanceValid(layer))
        {
            layer.Free();
        }
        layer = null;
        root = null;
        entries = null;
        statusLabel = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void Open()
    {
        EnsureInitialized();
        if (IsOpen)
        {
            return;
        }
        modalInput = input.AcquireScope(
            this,
            new InputScopeOptions(
                [MajoInputActions.Menu, MajoInputActions.Cancel,
                 MajoInputActions.OptionUp, MajoInputActions.OptionDown,
                 MajoInputActions.Confirm],
                isModal: true,
                name: "save-load-modal"));
        modalInput.Activated += HandleModalInput;
        pendingConfirmation = null;
        IsOpen = true;
        if (root is not null)
        {
            root.Visible = true;
        }
        _ = RebuildAsync();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        if (modalInput is not null)
        {
            modalInput.Activated -= HandleModalInput;
            modalInput.Dispose();
            modalInput = null;
        }
        pendingConfirmation = null;
        IsOpen = false;
        if (root is not null)
        {
            root.Visible = false;
        }
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private Control BuildView()
    {
        if (scene is null)
        {
            return BuildGeneratedView();
        }
        var panel = scene.Instantiate<Control>();
        entries = panel.GetNodeOrNull<VBoxContainer>("Margin/Layout/Scroll/Entries") ??
            throw new SaveSlotException(
                "Save/load scene is missing Margin/Layout/Scroll/Entries.");
        statusLabel = panel.GetNodeOrNull<Label>("Margin/Layout/StatusLabel") ??
            throw new SaveSlotException(
                "Save/load scene is missing Margin/Layout/StatusLabel.");
        var quick = panel.GetNodeOrNull<Button>("Margin/Layout/Header/QuickSaveButton") ??
            throw new SaveSlotException(
                "Save/load scene is missing Margin/Layout/Header/QuickSaveButton.");
        var close = panel.GetNodeOrNull<Button>("Margin/Layout/Header/CloseButton") ??
            throw new SaveSlotException(
                "Save/load scene is missing Margin/Layout/Header/CloseButton.");
        quick.Pressed += () => _ = QuickSaveFromUiAsync();
        close.Pressed += () => _ = CloseFromUiAsync();
        return panel;
    }

    private Control BuildGeneratedView()
    {
        var panel = new PanelContainer
        {
            Name = "MajoSaveLoadOverlay",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000,
        };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 40);
        }
        panel.AddChild(margin);
        var layout = new VBoxContainer();
        margin.AddChild(layout);
        var header = new HBoxContainer();
        layout.AddChild(header);
        header.AddChild(new Label
        {
            Text = TrText(UiStringKeys.SaveLoadTitle, "Save / Load"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        var quick = new Button { Text = TrText(UiStringKeys.SaveLoadQuickSave, "Quick Save") };
        quick.Pressed += () => _ = QuickSaveFromUiAsync();
        header.AddChild(quick);
        var close = new Button { Text = TrText(UiStringKeys.SaveLoadClose, "Close") };
        close.Pressed += () => _ = CloseFromUiAsync();
        header.AddChild(close);
        statusLabel = new Label { Text = TrText(UiStringKeys.SaveLoadReady, "Ready") };
        layout.AddChild(statusLabel);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddChild(scroll);
        entries = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(entries);
        return panel;
    }

    private static string TrText(string key, string fallback) => fallback;

    private async Task RebuildAsync()
    {
        if (!IsOpen || entries is null)
        {
            return;
        }
        try
        {
            var saved = await slots.ListAsync(cancellationToken: lifetime.Token);
            if (!IsOpen || entries is null)
            {
                return;
            }
            foreach (var child in entries.GetChildren())
            {
                child.Free();
            }
            var byId = saved.ToDictionary(value => value.Id);
            AddSection(TrText(UiStringKeys.SaveLoadSectionManual, "Manual"), SaveSlotKind.Manual, byId, includeEmpty: true);
            AddSection(TrText(UiStringKeys.SaveLoadSectionQuick, "Quick"), SaveSlotKind.Quick, byId, includeEmpty: false);
            AddSection(TrText(UiStringKeys.SaveLoadSectionAuto, "Auto"), SaveSlotKind.Auto, byId, includeEmpty: false);
            entries.GetChildren().OfType<Button>().FirstOrDefault()?.GrabFocus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Could not list saves: {exception.Message}");
        }
    }

    private void AddSection(
        string title,
        SaveSlotKind kind,
        IReadOnlyDictionary<SaveSlotId, SaveSlotMetadata> saved,
        bool includeEmpty)
    {
        if (entries is null)
        {
            return;
        }
        entries.AddChild(new Label { Text = title });
        var indexes = includeEmpty
            ? Enumerable.Range(1, slots.Capacities[kind])
            : saved.Keys.Where(id => id.Kind == kind).Select(id => id.Index).Order();
        var any = false;
        foreach (var index in indexes)
        {
            any = true;
            var id = new SaveSlotId(kind, index);
            saved.TryGetValue(id, out var metadata);
            entries.AddChild(BuildSlotRow(id, metadata));
        }
        if (!any)
        {
            entries.AddChild(new Label { Text = TrText(UiStringKeys.SaveLoadEmptySaves, "No saves.") });
        }
    }

    private Control BuildSlotRow(SaveSlotId id, SaveSlotMetadata? metadata)
    {
        var row = new HBoxContainer();
        var emptyText = TrText(UiStringKeys.SaveLoadSlotEmpty, "Empty");
        var label = new Label
        {
            Text = metadata is null
                ? $"{id.Index:D2}  {emptyText}"
                : FormatMetadata(metadata),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        row.AddChild(label);
        if (id.Kind == SaveSlotKind.Manual)
        {
            var saveText = metadata is null ? TrText(UiStringKeys.SaveLoadBtnSave, "Save") : TrText(UiStringKeys.SaveLoadBtnOverwrite, "Overwrite");
            var save = new Button { Text = saveText };
            save.Disabled = !CanSaveFromMenu(id);
            save.Pressed += () => ConfirmOrRun(
                id,
                saveText,
                metadata is not null,
                () => SaveFromUiAsync(id));
            row.AddChild(save);
        }
        if (metadata is not null)
        {
            var load = new Button
            {
                Text = TrText(UiStringKeys.SaveLoadBtnLoad, "Load"),
                Disabled = metadata.Compatibility != SaveSlotCompatibility.Compatible,
            };
            load.Pressed += () => _ = LoadFromUiAsync(id);
            row.AddChild(load);
            if (id.Kind == SaveSlotKind.Manual)
            {
                var name = new LineEdit
                {
                    Text = metadata.PlayerName ?? string.Empty,
                    PlaceholderText = TrText(UiStringKeys.SaveLoadPlaceholderName, "Save name"),
                    CustomMinimumSize = new Vector2(140, 0),
                };
                row.AddChild(name);
                var rename = new Button { Text = TrText(UiStringKeys.SaveLoadBtnRename, "Rename") };
                rename.Pressed += () => _ = RenameFromUiAsync(id, name.Text);
                row.AddChild(rename);
            }
            var deleteText = TrText(UiStringKeys.SaveLoadBtnDelete, "Delete");
            var delete = new Button { Text = deleteText };
            delete.Pressed += () => ConfirmOrRun(
                id,
                deleteText,
                true,
                () => DeleteFromUiAsync(id));
            row.AddChild(delete);
        }
        return row;
    }

    private void ConfirmOrRun(
        SaveSlotId slot,
        string action,
        bool requiresConfirmation,
        Func<Task> run)
    {
        if (!requiresConfirmation)
        {
            _ = run();
            return;
        }
        if (pendingConfirmation != (slot, action))
        {
            pendingConfirmation = (slot, action);
            var confirmTemplate = TrText(UiStringKeys.SaveLoadConfirm, "Press {0} again to confirm {1}.");
            SetStatus(string.Format(confirmTemplate, action, slot));
            return;
        }
        pendingConfirmation = null;
        _ = run();
    }

    private async Task SaveFromUiAsync(SaveSlotId id)
    {
        using var advanceGuard = BeginAdvanceGuard();
        using var playbackPause = playback?.AcquirePause(PlaybackPauseReason.External);
        Close();
        try
        {
            await slots.SaveAsync(id, cancellationToken: lifetime.Token);
        }
        catch (Exception exception)
        {
            SetStatus($"Save failed: {exception.Message}");
        }
        finally
        {
            await DrainInitiatingInputAsync();
        }
    }

    private async Task QuickSaveFromUiAsync()
    {
        using var advanceGuard = BeginAdvanceGuard();
        using var playbackPause = playback?.AcquirePause(PlaybackPauseReason.External);
        Close();
        try
        {
            await slots.SaveRotatingAsync(SaveSlotKind.Quick, cancellationToken: lifetime.Token);
        }
        catch (Exception exception)
        {
            SetStatus($"Quick save failed: {exception.Message}");
        }
        finally
        {
            await DrainInitiatingInputAsync();
        }
    }

    private async Task LoadFromUiAsync(SaveSlotId id)
    {
        using var advanceGuard = BeginAdvanceGuard();
        playback?.SetMode(PlaybackMode.Manual);
        using var playbackPause = playback?.AcquirePause(PlaybackPauseReason.StateRestore);
        Close();
        try
        {
            await slots.LoadAsync(id, lifetime.Token);
        }
        catch (Exception exception)
        {
            SetStatus($"Load failed: {exception.Message}");
        }
        finally
        {
            await DrainInitiatingInputAsync();
        }
    }

    private async Task CloseFromUiAsync()
    {
        using var advanceGuard = BeginAdvanceGuard();
        Close();
        await DrainInitiatingInputAsync();
    }

    private IInputHandle BeginAdvanceGuard() => input.AcquireMuter(
        this,
        new InputMuteOptions(
            [MajoInputActions.Advance],
            "save-load-ui-advance-guard"));

    private async Task DrainInitiatingInputAsync()
    {
        try
        {
            if (frameClock is not null)
            {
                await frameClock.NextFrameAsync(lifetime.Token);
                await frameClock.NextFrameAsync(lifetime.Token);
            }
            else if (owner.IsInsideTree())
            {
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DeleteFromUiAsync(SaveSlotId id)
    {
        try
        {
            await slots.DeleteAsync(id, lifetime.Token);
            await RebuildAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Delete failed: {exception.Message}");
        }
    }

    private async Task RenameFromUiAsync(SaveSlotId id, string? currentName)
    {
        try
        {
            await slots.RenameAsync(id, currentName, lifetime.Token);
            await RebuildAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Rename failed: {exception.Message}");
        }
    }

    private void HandleSlotsChanged()
    {
        if (IsOpen)
        {
            _ = RebuildAsync();
        }
    }

    private void HandleOperationProgressed(SaveSlotOperationInfo operation) =>
        SetStatus($"{operation.Status}: {operation.Phase}");

    private void HandleOperationFailed(SaveSlotOperationInfo operation) =>
        SetStatus($"Failed: {operation.FailureMessage}");

    private void HandleToggleInput(InputActivation _) => Toggle();

    private void HandleModalInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.Menu ||
            activation.Action == MajoInputActions.Cancel)
        {
            Close();
            return;
        }
        var focus = root?.GetViewport()?.GuiGetFocusOwner();
        if (activation.Action == MajoInputActions.OptionUp)
        {
            focus?.FindPrevValidFocus()?.GrabFocus();
        }
        else if (activation.Action == MajoInputActions.OptionDown)
        {
            focus?.FindNextValidFocus()?.GrabFocus();
        }
        else if (activation.Action == MajoInputActions.Confirm && focus is BaseButton button)
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private void EnsureToggleInput()
    {
        if (toggleInput is { IsDisposed: false })
        {
            return;
        }
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
        }
        toggleInput = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.Menu], name: "save-load-toggle"));
        toggleInput.Activated += HandleToggleInput;
    }

    private static string FormatMetadata(SaveSlotMetadata metadata)
    {
        if (metadata.Compatibility != SaveSlotCompatibility.Compatible)
        {
            return $"{metadata.Id.Index:D2}  {metadata.Compatibility}: {metadata.FailureMessage}";
        }
        var name = string.IsNullOrWhiteSpace(metadata.PlayerName)
            ? string.Empty
            : $"{metadata.PlayerName} — ";
        var text = string.IsNullOrWhiteSpace(metadata.TextSummary)
            ? metadata.LineId ?? "Unknown location"
            : metadata.TextSummary;
        return $"{metadata.Id.Index:D2}  {name}{text}  " +
            $"[{metadata.SavedAt?.ToLocalTime():g}]";
    }

    private bool CanSaveFromMenu(SaveSlotId id)
    {
        var availability = slots.CanSave(id);
        return availability.IsAllowed || availability.Reasons.All(reason =>
            reason.Code == SaveSlotUnavailableReasonCode.StateIneligible &&
            reason.StateReasonCode ==
                Majo.Runtime.State.StateEligibilityReasonCode.ModalInput);
    }

    private void SetStatus(string text)
    {
        if (statusLabel is not null)
        {
            statusLabel.Text = text;
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new SaveSlotException("Save/load overlay is not initialized.");
        }
    }
}

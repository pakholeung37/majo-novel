#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Settings;

namespace Majo.Runtime.UI.Settings;

public interface ISettingsOverlay : IRuntimeService
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void Toggle();
}

public sealed class DefaultSettingsOverlay : ISettingsOverlay
{
    private readonly Node owner;
    private readonly ISettingsManager settings;
    private readonly IInputManager input;
    private readonly PackedScene? scene;
    private CanvasLayer? layer;
    private Control? root;
    private Label? status;
    private IInputHandle? toggleInput;
    private IInputHandle? modalInput;
    private bool isInitialized;
    private bool refreshing;

    public DefaultSettingsOverlay(
        Node owner,
        ISettingsManager settings,
        IInputManager input,
        PackedScene? scene = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.scene = scene;
    }

    public bool IsOpen { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized) return Task.CompletedTask;
        root = scene?.Instantiate<Control>() ?? BuildGeneratedView();
        root.Name = "MajoSettingsOverlay";
        root.Visible = false;
        layer = new CanvasLayer { Name = "MajoSettingsLayer", Layer = 50 };
        owner.AddChild(layer);
        layer.AddChild(root);
        settings.Changed += HandleSettingsChanged;
        settings.PersistenceFailed += HandlePersistenceFailed;
        EnsureToggleInput();
        isInitialized = true;
        RefreshView();
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
        if (!isInitialized) return Task.CompletedTask;
        Close();
        settings.Changed -= HandleSettingsChanged;
        settings.PersistenceFailed -= HandlePersistenceFailed;
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggle;
            toggleInput.Dispose();
            toggleInput = null;
        }
        if (layer is not null && GodotObject.IsInstanceValid(layer))
        {
            layer.Free();
        }
        else if (root is not null && GodotObject.IsInstanceValid(root))
        {
            root.Free();
        }
        layer = null;
        root = null;
        status = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void Open()
    {
        if (!isInitialized) throw new InvalidOperationException("Settings UI is not initialized.");
        if (IsOpen) return;
        modalInput = input.AcquireScope(this, new InputScopeOptions(
            [MajoInputActions.Settings, MajoInputActions.Cancel,
             MajoInputActions.OptionUp, MajoInputActions.OptionDown,
             MajoInputActions.Confirm],
            isModal: true,
            name: "settings-modal"));
        modalInput.Activated += HandleModalInput;
        IsOpen = true;
        root!.Visible = true;
        RefreshView();
        root.FindChild("MasterVolume", recursive: true, owned: false)?.Call("grab_focus");
    }

    public void Close()
    {
        if (!IsOpen) return;
        if (modalInput is not null)
        {
            modalInput.Activated -= HandleModalInput;
            modalInput.Dispose();
            modalInput = null;
        }
        IsOpen = false;
        if (root is not null) root.Visible = false;
    }

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    private Control BuildGeneratedView()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 48);
        panel.AddChild(margin);
        var layout = new VBoxContainer();
        margin.AddChild(layout);
        var header = new HBoxContainer();
        layout.AddChild(header);
        header.AddChild(new Label { Text = "Settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var reset = new Button { Text = "Reset Defaults" };
        reset.Pressed += settings.ResetToDefaults;
        header.AddChild(reset);
        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        header.AddChild(close);
        AddSlider(layout, "Master", "MasterVolume", value => Update(s => s with { MasterVolume = (float)value }));
        AddSlider(layout, "BGM", "BgmVolume", value => Update(s => s with { BgmVolume = (float)value }));
        AddSlider(layout, "SFX", "SfxVolume", value => Update(s => s with { SfxVolume = (float)value }));
        AddSlider(layout, "Voice", "VoiceVolume", value => Update(s => s with { VoiceVolume = (float)value }));
        AddCheck(layout, "Mute Master", "MasterMuted", value => Update(s => s with { MasterMuted = value }));
        AddCheck(layout, "Mute BGM", "BgmMuted", value => Update(s => s with { BgmMuted = value }));
        AddCheck(layout, "Mute SFX", "SfxMuted", value => Update(s => s with { SfxMuted = value }));
        AddCheck(layout, "Mute Voice", "VoiceMuted", value => Update(s => s with { VoiceMuted = value }));
        AddCheck(layout, "Wait for voice in Auto", "AutoWaitForVoice", value => Update(s => s with { AutoWaitForVoice = value }));
        AddSpin(layout, "Letters / second", "LettersPerSecond", 1, 240,
            value => Update(s => s with { LettersPerSecond = (int)value }));
        AddSpin(layout, "Auto extra delay", "AutoDelay", 0, 30,
            value => Update(s => s with { AutoExtraDelaySeconds = value }));
        status = new Label { Name = "Status", Text = "Changes save automatically." };
        layout.AddChild(status);
        return panel;
    }

    private void AddSlider(VBoxContainer parent, string label, string name, Action<double> changed)
    {
        var row = NewRow(parent, label);
        var slider = new HSlider { Name = name, MinValue = 0, MaxValue = 1, Step = 0.01,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        slider.ValueChanged += value => { if (!refreshing) changed(value); };
        row.AddChild(slider);
    }

    private void AddSpin(VBoxContainer parent, string label, string name,
        double min, double max, Action<double> changed)
    {
        var row = NewRow(parent, label);
        var spin = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = 0.05 };
        spin.ValueChanged += value => { if (!refreshing) changed(value); };
        row.AddChild(spin);
    }

    private void AddCheck(VBoxContainer parent, string label, string name, Action<bool> changed)
    {
        var check = new CheckButton { Name = name, Text = label };
        check.Toggled += value => { if (!refreshing) changed(value); };
        parent.AddChild(check);
    }

    private static HBoxContainer NewRow(VBoxContainer parent, string label)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(180, 0) });
        parent.AddChild(row);
        return row;
    }

    private void Update(Func<RuntimeSettings, RuntimeSettings> update) =>
        settings.Update(update(settings.Current));

    private void RefreshView()
    {
        if (root is null) return;
        refreshing = true;
        try
        {
            SetRange("MasterVolume", settings.Current.MasterVolume);
            SetRange("BgmVolume", settings.Current.BgmVolume);
            SetRange("SfxVolume", settings.Current.SfxVolume);
            SetRange("VoiceVolume", settings.Current.VoiceVolume);
            SetRange("LettersPerSecond", settings.Current.LettersPerSecond);
            SetRange("AutoDelay", settings.Current.AutoExtraDelaySeconds);
            if (root.FindChild("MasterMuted", true, false) is BaseButton mute)
                mute.ButtonPressed = settings.Current.MasterMuted;
            if (root.FindChild("BgmMuted", true, false) is BaseButton bgmMute)
                bgmMute.ButtonPressed = settings.Current.BgmMuted;
            if (root.FindChild("SfxMuted", true, false) is BaseButton sfxMute)
                sfxMute.ButtonPressed = settings.Current.SfxMuted;
            if (root.FindChild("VoiceMuted", true, false) is BaseButton voiceMute)
                voiceMute.ButtonPressed = settings.Current.VoiceMuted;
            if (root.FindChild("AutoWaitForVoice", true, false) is BaseButton wait)
                wait.ButtonPressed = settings.Current.AutoWaitForVoice;
            status ??= root.FindChild("Status", true, false) as Label;
            if (status is not null)
            {
                status.Text = settings.RecoveryStatus.Kind == ScopedStateRecoveryKind.RecoveredFromCorruption
                    ? "Damaged settings were isolated; project defaults are active."
                    : settings.IsDirty ? "Saving changes…" : "Settings saved.";
            }
        }
        finally { refreshing = false; }
    }

    private void SetRange(string name, double value)
    {
        if (root!.FindChild(name, true, false) is Godot.Range range) range.Value = value;
    }

    private void EnsureToggleInput()
    {
        if (toggleInput is { IsDisposed: false }) return;
        toggleInput = input.AcquireScope(this,
            new InputScopeOptions([MajoInputActions.Settings], name: "settings-toggle"));
        toggleInput.Activated += HandleToggle;
    }

    private void HandleToggle(InputActivation _) => Toggle();
    private void HandleModalInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.Cancel ||
            activation.Action == MajoInputActions.Settings) Close();
    }
    private void HandleSettingsChanged(SettingsChanged _) => RefreshView();
    private void HandlePersistenceFailed(Exception exception)
    {
        if (status is not null) status.Text = $"Could not save settings: {exception.Message}";
    }
}

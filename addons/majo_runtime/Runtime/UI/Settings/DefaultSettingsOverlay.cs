#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Settings;

namespace Majo.Runtime.UI.Settings;

public interface ISettingsOverlay : IRuntimeService
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void Toggle();
}

public sealed class DefaultSettingsOverlay : ISettingsOverlay, IDisposable
{
    private readonly Node owner;
    private readonly ISettingsManager settings;
    private readonly IInputManager input;
    private readonly ILocaleManager? localeManager;
    private readonly PackedScene? scene;
    private CanvasLayer? layer;
    private Control? root;
    private Label? titleLabel;
    private Button? resetButton;
    private Button? closeButton;
    private Label? status;
    private OptionButton? languageOption;
    private OptionButton? voiceLanguageOption;
    private IInputHandle? toggleInput;
    private IInputHandle? modalInput;
    private bool isInitialized;
    private bool refreshing;

    public DefaultSettingsOverlay(
        Node owner,
        ISettingsManager settings,
        IInputManager input,
        ILocaleManager? localeManager = null,
        PackedScene? scene = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.localeManager = localeManager;
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
        if (IsOpen || root is null) return;
        RefreshView();
        IsOpen = true;
        root.Visible = true;
        if (modalInput is null)
        {
            modalInput = input.AcquireScope(this,
                new InputScopeOptions(
                    [MajoInputActions.Cancel, MajoInputActions.Settings,
                     MajoInputActions.OptionUp, MajoInputActions.OptionDown,
                     MajoInputActions.Confirm],
                    isModal: true, name: "settings-modal"));
            modalInput.Activated += HandleModalInput;
        }
        root.FindChild("DialogueLanguageOption", recursive: true, owned: false)?.Call("grab_focus");
    }

    public void Close()
    {
        if (!IsOpen || root is null) return;
        IsOpen = false;
        root.Visible = false;
        if (modalInput is not null)
        {
            modalInput.Activated -= HandleModalInput;
            modalInput.Dispose();
            modalInput = null;
        }
    }

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    private PanelContainer BuildGeneratedView()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 48);
        panel.AddChild(margin);

        var outerLayout = new VBoxContainer();
        margin.AddChild(outerLayout);

        var header = new HBoxContainer();
        outerLayout.AddChild(header);
        titleLabel = new Label { Text = TrText(UiStringKeys.SettingsTitle, "Settings"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddChild(titleLabel);
        resetButton = new Button { Text = TrText(UiStringKeys.SettingsReset, "Reset Defaults") };
        resetButton.Pressed += settings.ResetToDefaults;
        header.AddChild(resetButton);
        closeButton = new Button { Text = TrText(UiStringKeys.SettingsClose, "Close") };
        closeButton.Pressed += Close;
        header.AddChild(closeButton);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        outerLayout.AddChild(scroll);

        var layout = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(layout);

        AddLocaleSelectors(layout);

        AddSlider(layout, UiStringKeys.SettingsMaster, "Master", "MasterVolume", value => Update(s => s with { MasterVolume = (float)value }));
        AddSlider(layout, UiStringKeys.SettingsBgm, "BGM", "BgmVolume", value => Update(s => s with { BgmVolume = (float)value }));
        AddSlider(layout, UiStringKeys.SettingsSfx, "SFX", "SfxVolume", value => Update(s => s with { SfxVolume = (float)value }));
        AddSlider(layout, UiStringKeys.SettingsVoice, "Voice", "VoiceVolume", value => Update(s => s with { VoiceVolume = (float)value }));
        AddCheck(layout, UiStringKeys.SettingsMuteMaster, "Mute Master", "MasterMuted", value => Update(s => s with { MasterMuted = value }));
        AddCheck(layout, UiStringKeys.SettingsMuteBgm, "Mute BGM", "BgmMuted", value => Update(s => s with { BgmMuted = value }));
        AddCheck(layout, UiStringKeys.SettingsMuteSfx, "Mute SFX", "SfxMuted", value => Update(s => s with { SfxMuted = value }));
        AddCheck(layout, UiStringKeys.SettingsMuteVoice, "Mute Voice", "VoiceMuted", value => Update(s => s with { VoiceMuted = value }));
        AddCheck(layout, UiStringKeys.SettingsAutoWaitVoice, "Wait for voice in Auto", "AutoWaitForVoice", value => Update(s => s with { AutoWaitForVoice = value }));
        AddSpin(layout, UiStringKeys.SettingsLettersPerSecond, "Letters / second", "LettersPerSecond", 1, 240, 1,
            value => Update(s => s with { LettersPerSecond = (int)value }));
        AddSpin(layout, UiStringKeys.SettingsAutoDelay, "Auto extra delay", "AutoDelay", 0, 30, 0.05,
            value => Update(s => s with { AutoExtraDelaySeconds = value }));
        AddSlider(layout, UiStringKeys.SettingsFontScale, "Text Scale", "FontScale", value => Update(s => s with { FontScale = value }), 0.5, 2.0, 0.05);
        AddSpin(layout, UiStringKeys.SettingsLineSpacing, "Line Spacing", "LineSpacing", 0, 32, 1,
            value => Update(s => s with { LineSpacing = (int)value }));

        status = new Label { Name = "Status", Text = TrText(UiStringKeys.SettingsStatusSaved, "Settings saved.") };
        outerLayout.AddChild(status);
        return panel;
    }

    private void AddLocaleSelectors(VBoxContainer parent)
    {
        languageOption = AddLocaleSelector(
            parent,
            "Dialogue Language",
            "DialogueLanguageOption",
            selected => Update(s => s with { DialogueLocale = selected }));
        voiceLanguageOption = AddLocaleSelector(
            parent,
            "Voice Language",
            "VoiceLanguageOption",
            selected => Update(s => s with { VoiceLocale = selected }));
    }

    private OptionButton AddLocaleSelector(
        VBoxContainer parent,
        string label,
        string name,
        Action<string> selected)
    {
        var row = NewRow(parent, string.Empty, label);
        var option = new OptionButton
        {
            Name = name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var available = localeManager?.AvailableLocales ?? ["en", "zh-CN"];
        for (var i = 0; i < available.Count; i++)
        {
            option.AddItem(available[i], i);
        }

        option.ItemSelected += index =>
        {
            if (refreshing) return;
            selected(option.GetItemText((int)index));
        };
        row.AddChild(option);
        return option;
    }

    private void AddSlider(
        VBoxContainer parent,
        string key,
        string defaultLabel,
        string name,
        Action<double> changed,
        double min = 0,
        double max = 1,
        double step = 0.01)
    {
        var row = NewRow(parent, key, defaultLabel);
        var slider = new HSlider
        {
            Name = name,
            MinValue = min,
            MaxValue = max,
            Step = step,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        slider.ValueChanged += value => { if (!refreshing) changed(value); };
        row.AddChild(slider);
    }

    private void AddSpin(
        VBoxContainer parent,
        string key,
        string defaultLabel,
        string name,
        double min,
        double max,
        double step,
        Action<double> changed)
    {
        var row = NewRow(parent, key, defaultLabel);
        var spin = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = step };
        spin.ValueChanged += value => { if (!refreshing) changed(value); };
        row.AddChild(spin);
    }

    private void AddCheck(VBoxContainer parent, string key, string defaultLabel, string name, Action<bool> changed)
    {
        var check = new CheckButton { Name = name, Text = TrText(key, defaultLabel) };
        check.SetMeta("tr_key", key);
        check.SetMeta("default_label", defaultLabel);
        check.Toggled += value => { if (!refreshing) changed(value); };
        parent.AddChild(check);
    }

    private static HBoxContainer NewRow(VBoxContainer parent, string key, string defaultLabel)
    {
        var row = new HBoxContainer();
        var label = new Label
        {
            Text = TrText(key, defaultLabel),
            CustomMinimumSize = new Vector2(220, 0),
        };
        label.SetMeta("tr_key", key);
        label.SetMeta("default_label", defaultLabel);
        row.AddChild(label);
        parent.AddChild(row);
        return row;
    }

    private static string TrText(string key, string fallback) => fallback;

    private void Update(Func<RuntimeSettings, RuntimeSettings> update) =>
        settings.Update(update(settings.Current));

    private void RefreshView()
    {
        if (root is null) return;
        refreshing = true;
        try
        {
            if (titleLabel is not null) titleLabel.Text = TrText(UiStringKeys.SettingsTitle, "Settings");
            if (resetButton is not null) resetButton.Text = TrText(UiStringKeys.SettingsReset, "Reset Defaults");
            if (closeButton is not null) closeButton.Text = TrText(UiStringKeys.SettingsClose, "Close");

            RefreshDynamicLabels(root);

            SetRange("MasterVolume", settings.Current.MasterVolume);
            SetRange("BgmVolume", settings.Current.BgmVolume);
            SetRange("SfxVolume", settings.Current.SfxVolume);
            SetRange("VoiceVolume", settings.Current.VoiceVolume);
            SetRange("LettersPerSecond", settings.Current.LettersPerSecond);
            SetRange("AutoDelay", settings.Current.AutoExtraDelaySeconds);
            SetRange("FontScale", settings.Current.FontScale);
            SetRange("LineSpacing", settings.Current.LineSpacing);

            if (languageOption is not null)
            {
                var available = localeManager?.AvailableLocales ?? ["en", "zh-CN"];
                for (var i = 0; i < available.Count; i++)
                {
                    if (string.Equals(available[i], settings.Current.DialogueLocale, StringComparison.OrdinalIgnoreCase))
                    {
                        languageOption.Selected = i;
                        break;
                    }
                }
            }

            if (voiceLanguageOption is not null)
            {
                var available = localeManager?.AvailableLocales ?? ["en", "zh-CN"];
                for (var i = 0; i < available.Count; i++)
                {
                    if (string.Equals(available[i], settings.Current.VoiceLocale, StringComparison.OrdinalIgnoreCase))
                    {
                        voiceLanguageOption.Selected = i;
                        break;
                    }
                }
            }

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
                    ? TrText(UiStringKeys.SettingsStatusCorrupt, "Damaged settings were isolated; project defaults are active.")
                    : settings.IsDirty
                        ? TrText(UiStringKeys.SettingsStatusSaving, "Saving changes…")
                        : TrText(UiStringKeys.SettingsStatusSaved, "Settings saved.");
            }
        }
        finally { refreshing = false; }
    }

    private static void RefreshDynamicLabels(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Label label && label.HasMeta("tr_key"))
            {
                var key = label.GetMeta("tr_key").AsString();
                var fallback = label.GetMeta("default_label").AsString();
                label.Text = TrText(key, fallback);
            }
            else if (child is Button button && button.HasMeta("tr_key"))
            {
                var key = button.GetMeta("tr_key").AsString();
                var fallback = button.GetMeta("default_label").AsString();
                button.Text = TrText(key, fallback);
            }
            RefreshDynamicLabels(child);
        }
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

    public void Dispose()
    {
        toggleInput?.Dispose();
        modalInput?.Dispose();
        if (layer is not null && GodotObject.IsInstanceValid(layer))
        {
            layer.Free();
            layer.Dispose();
        }
        else if (root is not null && GodotObject.IsInstanceValid(root))
        {
            root.Free();
            root.Dispose();
        }

        layer = null;
        root = null;
        titleLabel = null;
        resetButton = null;
        closeButton = null;
        status = null;
        languageOption = null;
        voiceLanguageOption = null;
    }
}

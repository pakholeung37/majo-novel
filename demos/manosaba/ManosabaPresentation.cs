#nullable enable

using System;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.History;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

namespace Majo.Demos.Manosaba;

public partial class ManosabaPresentation : Control
{
    private Control? menuPanel;
    private TextureButton? autoButton;
    private TextureButton? menuButton;
    private TextureButton? closeButton;
    private TextureButton? saveButton;
    private TextureButton? loadButton;
    private TextureButton? historyButton;
    private TextureButton? optionsButton;
    private TextureButton? titleButton;
    private Label? menuStatus;
    private TextureRect? inputIndicator;
    private RichTextLabel? autoLabel;
    private TextureRect? grassLeft;
    private TextureRect? grassRight;
    private TextureRect? bottomFrame;
    private TextureRect? topFrame;
    private TextureRect? foreGround;
    private IPlaybackModeManager? playback;
    private IHistoryManager? history;
    private ISaveLoadOverlay? saveLoad;
    private ISettingsOverlay? settings;
    private IAudioManager? audio;
    private IPlaybackPauseHandle? menuPause;
    private Tween? menuButtonHoverTween;
    private Tween? autoButtonHoverTween;
    private Tween? autoLabelTween;
    private Tween? inputIndicatorTween;
    private Tween? frameTransitionTween;
    private static readonly Vector2 MenuButtonHoverScale = new(1.08f, 1.08f);
    private static readonly Vector2 AutoButtonBaseScale = new(0.8f, 0.8f);
    private static readonly Vector2 AutoButtonHoverScale = new(0.8f * 1.08f, 0.8f * 1.08f);
    private const float AutoLabelOffOffsetLeft = -51.572f;
    private const float AutoLabelOffOffsetRight = 51.428f;
    private const float AutoLabelOnShiftX = 52.0f;

    [Export] public AudioStream? AutoSubmitOnSound { get; set; }
    [Export] public AudioStream? AutoSubmitOffSound { get; set; }
    [Export] public AudioStream? MenuOpenSound { get; set; }
    [Export] public AudioStream? MenuCloseSound { get; set; }

    private const string DefaultAutoSubmitOnSoundPath =
        "res://demos/manosaba/assets/audio/system/Sfx_System_Submit_001.wav";
    private const string DefaultAutoSubmitOffSoundPath =
        "res://demos/manosaba/assets/audio/system/Sfx_System_Submit_003a.wav";
    private const string DefaultMenuOpenSoundPath =
        "res://demos/manosaba/assets/audio/system/Sfx_System_Open_001.wav";
    private const string DefaultMenuCloseSoundPath =
        "res://demos/manosaba/assets/audio/system/Sfx_System_Close_001.wav";

    public override void _Ready()
    {
        menuPanel = GetNode<Control>("MenuPanel");
        autoButton = GetNode<TextureButton>("LinePresenter/AutoButton");
        menuButton = GetNode<TextureButton>("LinePresenter/MenuButton");
        closeButton = GetNode<TextureButton>("MenuPanel/Phone/CloseButton");
        saveButton = GetNode<TextureButton>("MenuPanel/Phone/SaveButton");
        loadButton = GetNode<TextureButton>("MenuPanel/Phone/LoadButton");
        historyButton = GetNode<TextureButton>("MenuPanel/Phone/HistoryButton");
        optionsButton = GetNode<TextureButton>("MenuPanel/Phone/OptionsButton");
        titleButton = GetNode<TextureButton>("MenuPanel/Phone/TitleButton");
        menuStatus = GetNode<Label>("MenuPanel/Status");
        inputIndicator = GetNodeOrNull<TextureRect>("LinePresenter/InputIndicator");
        autoLabel = GetNodeOrNull<RichTextLabel>("LinePresenter/AutoButton/AutoLabel");
        grassLeft = GetNodeOrNull<TextureRect>("LinePresenter/GrassLeft");
        grassRight = GetNodeOrNull<TextureRect>("LinePresenter/GrassRight");
        bottomFrame = GetNodeOrNull<TextureRect>("LinePresenter/BottomFrame");
        topFrame = GetNodeOrNull<TextureRect>("LinePresenter/TopFrame");
        foreGround = GetNodeOrNull<TextureRect>("LinePresenter/ForeGround");

        AutoSubmitOnSound ??= ResourceLoader.Load<AudioStream>(DefaultAutoSubmitOnSoundPath);
        AutoSubmitOffSound ??= ResourceLoader.Load<AudioStream>(DefaultAutoSubmitOffSoundPath);
        MenuOpenSound ??= ResourceLoader.Load<AudioStream>(DefaultMenuOpenSoundPath);
        MenuCloseSound ??= ResourceLoader.Load<AudioStream>(DefaultMenuCloseSoundPath);

        menuButton.Pressed += ToggleMenu;
        closeButton.Pressed += () => CloseMenu();
        saveButton.Pressed += () => OnMenuPanelButtonPressed(OpenSaveLoad);
        loadButton.Pressed += () => OnMenuPanelButtonPressed(OpenSaveLoad);
        historyButton.Pressed += () => OnMenuPanelButtonPressed(OpenHistory);
        optionsButton.Pressed += () => OnMenuPanelButtonPressed(OpenSettings);
        titleButton.Pressed += () => OnMenuPanelButtonPressed(ShowTitleNotice);
        autoButton.Pressed += ToggleAuto;

        if (menuButton is not null)
        {
            menuButton.PivotOffset = menuButton.Size / 2;
            menuButton.Resized += () => menuButton.PivotOffset = menuButton.Size / 2;
            menuButton.MouseEntered += OnMenuButtonMouseEntered;
            menuButton.MouseExited += OnMenuButtonMouseExited;
        }

        if (autoButton is not null)
        {
            autoButton.PivotOffset = autoButton.Size / 2;
            autoButton.Resized += () => autoButton.PivotOffset = autoButton.Size / 2;
            autoButton.MouseEntered += OnAutoButtonMouseEntered;
            autoButton.MouseExited += OnAutoButtonMouseExited;
        }

        UpdateAutoLabelPosition(autoButton?.ButtonPressed == true, animate: false);
        StartInputIndicatorBlink();
        SetFrameMode(CurrentFrameMode, duration: 0);

        // // The ADV presenter owns a full-screen input scope. Keep the interactive
        // // art above it and stop GUI clicks before they can become Advance input.
        // foreach (var button in new[]
        // 		 {
        // 			 menuButton, closeButton, saveButton, loadButton,
        // 			 historyButton, optionsButton, titleButton, autoButton,
        // 		 })
        // {
        // 	button.MouseFilter = MouseFilterEnum.Stop;
        // 	button.ZIndex = 100;
        // }

        menuPanel.Visible = false;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } ||
            @event.IsActionPressed("ui_cancel"))
        {
            ToggleMenu();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventMouseButton
            {
                ButtonIndex: MouseButton.Left,
                Pressed: true,
            } mouse)
        {
            return;
        }

        if (menuPanel?.Visible == true)
        {
            if (IsPointerOver(closeButton, mouse.Position)) CloseMenu();
            else if (IsPointerOver(saveButton, mouse.Position)) OnMenuPanelButtonPressed(OpenSaveLoad);
            else if (IsPointerOver(loadButton, mouse.Position)) OnMenuPanelButtonPressed(OpenSaveLoad);
            else if (IsPointerOver(historyButton, mouse.Position)) OnMenuPanelButtonPressed(OpenHistory);
            else if (IsPointerOver(optionsButton, mouse.Position)) OnMenuPanelButtonPressed(OpenSettings);
            else if (IsPointerOver(titleButton, mouse.Position)) OnMenuPanelButtonPressed(ShowTitleNotice);
            else return;

            GetViewport().SetInputAsHandled();
        }
        else if (IsPointerOver(menuButton, mouse.Position))
        {
            ToggleMenu();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree()
    {
        if (playback is not null)
        {
            playback.ModeChanged -= HandlePlaybackModeChanged;
        }

        menuButtonHoverTween?.Kill();
        menuButtonHoverTween = null;

        autoButtonHoverTween?.Kill();
        autoButtonHoverTween = null;

        autoLabelTween?.Kill();
        autoLabelTween = null;

        inputIndicatorTween?.Kill();
        inputIndicatorTween = null;

        frameTransitionTween?.Kill();
        frameTransitionTween = null;

        menuPause?.Dispose();
        menuPause = null;
    }

    public void Bind(
        IPlaybackModeManager playbackModeManager,
        IHistoryManager historyManager,
        ISaveLoadOverlay saveLoadOverlay,
        ISettingsOverlay settingsOverlay,
        IAudioManager? audioManager = null)
    {
        playback = playbackModeManager;
        history = historyManager;
        saveLoad = saveLoadOverlay;
        settings = settingsOverlay;
        audio = audioManager;
        playback.ModeChanged += HandlePlaybackModeChanged;
        RefreshAutoState();
    }

    private void OnMenuPanelButtonPressed(Action action)
    {
        PlaySound(AutoSubmitOnSound);
        action();
    }

    private void ToggleMenu() => SetMenuVisible(menuPanel?.Visible != true, playSound: true);

    private void CloseMenu(bool playSound = true) => SetMenuVisible(false, playSound: playSound);

    private void SetMenuVisible(bool visible, bool playSound = true)
    {
        if (menuPanel is null || menuPanel.Visible == visible)
        {
            return;
        }

        menuPanel.Visible = visible;
        if (visible)
        {
            if (playSound)
            {
                PlaySound(MenuOpenSound);
            }
            AnimateMenuButtonScale(Vector2.One);
            AnimateAutoButtonScale(AutoButtonBaseScale);
            menuPause = playback?.AcquirePause(PlaybackPauseReason.External);
            if (menuStatus is not null)
            {
                menuStatus.Text = "右键或 Esc 关闭";
            }
        }
        else
        {
            if (playSound)
            {
                PlaySound(MenuCloseSound);
            }
            menuPause?.Dispose();
            menuPause = null;
        }
    }

    private void OnMenuButtonMouseEntered() => AnimateMenuButtonScale(MenuButtonHoverScale);

    private void OnMenuButtonMouseExited() => AnimateMenuButtonScale(Vector2.One);

    private void AnimateMenuButtonScale(Vector2 targetScale)
    {
        if (menuButton is null || !IsInsideTree())
        {
            return;
        }

        menuButtonHoverTween?.Kill();
        menuButtonHoverTween = CreateTween();
        menuButtonHoverTween
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        menuButtonHoverTween.TweenProperty(menuButton, "scale", targetScale, 0.15);
    }

    private void OnAutoButtonMouseEntered() => AnimateAutoButtonScale(AutoButtonHoverScale);

    private void OnAutoButtonMouseExited() => AnimateAutoButtonScale(AutoButtonBaseScale);

    private void AnimateAutoButtonScale(Vector2 targetScale)
    {
        if (autoButton is null || !IsInsideTree())
        {
            return;
        }

        autoButtonHoverTween?.Kill();
        autoButtonHoverTween = CreateTween();
        autoButtonHoverTween
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        autoButtonHoverTween.TweenProperty(autoButton, "scale", targetScale, 0.15);
    }

    private void PlaySound(AudioStream? stream)
    {
        if (stream is null || !IsInsideTree())
        {
            return;
        }

        var player = new AudioStreamPlayer
        {
            Stream = stream,
        };
        if (audio is not null)
        {
            if (audio.MasterMuted || audio.SfxMuted)
            {
                player.VolumeDb = -80;
            }
            else
            {
                var linear = Math.Clamp(audio.MasterVolume * audio.SfxVolume, 0f, 1f);
                player.VolumeDb = linear <= 0 ? -80 : Mathf.LinearToDb(linear);
            }
        }
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
    }

    private void StartInputIndicatorBlink()
    {
        if (inputIndicator is null || !IsInsideTree())
        {
            return;
        }

        inputIndicatorTween?.Kill();
        inputIndicatorTween = CreateTween();
        inputIndicatorTween.SetLoops();
        inputIndicatorTween
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        inputIndicatorTween.TweenProperty(inputIndicator, "modulate:a", 0.0f, 1.6);
        inputIndicatorTween.TweenProperty(inputIndicator, "modulate:a", 1.0f, 1.6);
    }

    private void ToggleAuto()
    {
        if (playback is not null)
        {
            var turningOn = playback.Mode != PlaybackMode.Auto;
            UpdateAutoLabelPosition(turningOn, animate: false);
            playback.ToggleAuto();
            PlaySound(turningOn ? AutoSubmitOnSound : AutoSubmitOffSound);
        }
        else if (autoButton is not null)
        {
            var turningOn = autoButton.ButtonPressed;
            UpdateAutoLabelPosition(turningOn, animate: false);
            PlaySound(turningOn ? AutoSubmitOnSound : AutoSubmitOffSound);
        }
    }

    private void HandlePlaybackModeChanged(PlaybackModeChanged _) =>
        CallDeferred(MethodName.RefreshAutoState);

    private void RefreshAutoState()
    {
        if (autoButton is not null && playback is not null)
        {
            var isAuto = playback.Mode == PlaybackMode.Auto;
            autoButton.ButtonPressed = isAuto;
            UpdateAutoLabelPosition(isAuto, animate: false);
        }
    }

    private void UpdateAutoLabelPosition(bool isAuto, bool animate = true)
    {
        if (autoLabel is null || !IsInsideTree())
        {
            return;
        }

        var targetOffsetLeft = isAuto
            ? AutoLabelOffOffsetLeft - AutoLabelOnShiftX
            : AutoLabelOffOffsetLeft;
        var targetOffsetRight = isAuto
            ? AutoLabelOffOffsetRight - AutoLabelOnShiftX
            : AutoLabelOffOffsetRight;

        autoLabelTween?.Kill();
        if (!animate)
        {
            autoLabel.OffsetLeft = targetOffsetLeft;
            autoLabel.OffsetRight = targetOffsetRight;
            return;
        }

        autoLabelTween = CreateTween();
        autoLabelTween
            .SetParallel(true)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        autoLabelTween.TweenProperty(autoLabel, "offset_left", targetOffsetLeft, 0.15);
        autoLabelTween.TweenProperty(autoLabel, "offset_right", targetOffsetRight, 0.15);
    }

    private void OpenSaveLoad()
    {
        CloseMenu(playSound: false);
        saveLoad?.Open();
    }

    private void OpenHistory()
    {
        CloseMenu(playSound: false);
        history?.Open();
    }

    private void OpenSettings()
    {
        CloseMenu(playSound: false);
        settings?.Open();
    }

    private void ShowTitleNotice()
    {
        if (menuStatus is not null)
        {
            menuStatus.Text = "返回标题画面将在标题流程接入后启用";
        }
    }

    public ManosabaFrameMode CurrentFrameMode { get; private set; } = ManosabaFrameMode.Default;

    public void SetFrameMode(ManosabaFrameMode mode, double duration = 0)
    {
        CurrentFrameMode = mode;
        var isStream = mode == ManosabaFrameMode.Stream;
        frameTransitionTween?.Kill();
        frameTransitionTween = null;

        var standardFrameNodes = new[] { grassLeft, grassRight, bottomFrame, topFrame };

        if (duration <= 0 || !IsInsideTree())
        {
            foreach (var node in standardFrameNodes)
            {
                if (node is not null)
                {
                    node.Visible = !isStream;
                    node.Modulate = Colors.White;
                }
            }

            if (foreGround is not null)
            {
                foreGround.Visible = isStream;
                foreGround.Modulate = Colors.White;
            }

            return;
        }

        var tween = CreateTween();
        tween.SetParallel(true);
        if (isStream)
        {
            if (foreGround is not null)
            {
                foreGround.Visible = true;
                foreGround.Modulate = new Color(1, 1, 1, 0);
                tween.TweenProperty(foreGround, "modulate:a", 1.0f, duration);
            }

            foreach (var node in standardFrameNodes)
            {
                if (node is not null && node.Visible)
                {
                    tween.TweenProperty(node, "modulate:a", 0.0f, duration);
                }
            }
        }
        else
        {
            foreach (var node in standardFrameNodes)
            {
                if (node is not null)
                {
                    node.Visible = true;
                    node.Modulate = new Color(1, 1, 1, 0);
                    tween.TweenProperty(node, "modulate:a", 1.0f, duration);
                }
            }

            if (foreGround is not null && foreGround.Visible)
            {
                tween.TweenProperty(foreGround, "modulate:a", 0.0f, duration);
            }
        }

        tween.Chain().TweenCallback(Callable.From(() =>
        {
            foreach (var node in standardFrameNodes)
            {
                if (node is not null)
                {
                    node.Visible = !isStream;
                    node.Modulate = Colors.White;
                }
            }

            if (foreGround is not null)
            {
                foreGround.Visible = isStream;
                foreGround.Modulate = Colors.White;
            }
        }));
        frameTransitionTween = tween;
    }

    private static bool IsPointerOver(Control? control, Vector2 viewportPosition)
    {
        if (control is null || !control.IsVisibleInTree())
        {
            return false;
        }

        var localPosition = control.GetGlobalTransformWithCanvas()
            .AffineInverse() * viewportPosition;
        return new Rect2(Vector2.Zero, control.Size).HasPoint(localPosition);
    }
}

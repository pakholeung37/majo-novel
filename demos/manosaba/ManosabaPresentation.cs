#nullable enable

using Godot;
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
	private IPlaybackModeManager? playback;
	private IHistoryManager? history;
	private ISaveLoadOverlay? saveLoad;
	private ISettingsOverlay? settings;
	private IPlaybackPauseHandle? menuPause;
	private Tween? menuButtonHoverTween;
	private Tween? inputIndicatorTween;
	private static readonly Vector2 MenuButtonHoverScale = new(1.08f, 1.08f);

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

		menuButton.Pressed += ToggleMenu;
		closeButton.Pressed += CloseMenu;
		saveButton.Pressed += OpenSaveLoad;
		loadButton.Pressed += OpenSaveLoad;
		historyButton.Pressed += OpenHistory;
		optionsButton.Pressed += OpenSettings;
		titleButton.Pressed += ShowTitleNotice;
		autoButton.Pressed += ToggleAuto;

		if (menuButton is not null)
		{
			menuButton.PivotOffset = menuButton.Size / 2;
			menuButton.Resized += () => menuButton.PivotOffset = menuButton.Size / 2;
			menuButton.MouseEntered += OnMenuButtonMouseEntered;
			menuButton.MouseExited += OnMenuButtonMouseExited;
		}

		StartInputIndicatorBlink();

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

	public override void _Input(InputEvent inputEvent)
	{
		if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } ||
			inputEvent.IsActionPressed("ui_cancel"))
		{
			ToggleMenu();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (inputEvent is not InputEventMouseButton
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
			else if (IsPointerOver(saveButton, mouse.Position)) OpenSaveLoad();
			else if (IsPointerOver(loadButton, mouse.Position)) OpenSaveLoad();
			else if (IsPointerOver(historyButton, mouse.Position)) OpenHistory();
			else if (IsPointerOver(optionsButton, mouse.Position)) OpenSettings();
			else if (IsPointerOver(titleButton, mouse.Position)) ShowTitleNotice();
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

		inputIndicatorTween?.Kill();
		inputIndicatorTween = null;

		menuPause?.Dispose();
		menuPause = null;
	}

	public void Bind(
		IPlaybackModeManager playbackModeManager,
		IHistoryManager historyManager,
		ISaveLoadOverlay saveLoadOverlay,
		ISettingsOverlay settingsOverlay)
	{
		playback = playbackModeManager;
		history = historyManager;
		saveLoad = saveLoadOverlay;
		settings = settingsOverlay;
		playback.ModeChanged += HandlePlaybackModeChanged;
		RefreshAutoState();
	}

	private void ToggleMenu() => SetMenuVisible(menuPanel?.Visible != true);

	private void CloseMenu() => SetMenuVisible(false);

	private void SetMenuVisible(bool visible)
	{
		if (menuPanel is null || menuPanel.Visible == visible)
		{
			return;
		}

		menuPanel.Visible = visible;
		if (visible)
		{
			AnimateMenuButtonScale(Vector2.One);
			menuPause = playback?.AcquirePause(PlaybackPauseReason.Modal);
			if (menuStatus is not null)
			{
				menuStatus.Text = "右键或 Esc 关闭";
			}
		}
		else
		{
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

	private void ToggleAuto() => playback?.ToggleAuto();

	private void HandlePlaybackModeChanged(PlaybackModeChanged _) =>
		CallDeferred(MethodName.RefreshAutoState);

	private void RefreshAutoState()
	{
		if (autoButton is not null && playback is not null)
		{
			autoButton.ButtonPressed = playback.Mode == PlaybackMode.Auto;
		}
	}

	private void OpenSaveLoad()
	{
		CloseMenu();
		saveLoad?.Open();
	}

	private void OpenHistory()
	{
		CloseMenu();
		history?.Open();
	}

	private void OpenSettings()
	{
		CloseMenu();
		settings?.Open();
	}

	private void ShowTitleNotice()
	{
		if (menuStatus is not null)
		{
			menuStatus.Text = "返回标题画面将在标题流程接入后启用";
		}
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

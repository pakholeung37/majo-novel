#nullable enable

using System;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

namespace Majo.Demos.Manosaba;

public partial class ManosabaDemoController : Node
{
	public override void _Ready()
	{
		RunDemoAsync();
	}

	private async void RunDemoAsync()
	{
		try
		{
			var configuration = MajoProjectConfiguration.CreateDefault();
			configuration.Resources!.RootPath = "res://demos/manosaba/assets";
			configuration.State!.StorageNamespace = "majo/manosaba";
			configuration.State.GameBuildId = "manosaba-v0.1";
			configuration.Save!.Enabled = true;
			configuration.Diagnostics!.EnableOverlay = false;
			configuration.Presentation!.PresenterScene =
				ResourceLoader.Load<PackedScene>(
					"res://demos/manosaba/ManosabaPresentation.tscn");
			configuration.Presentation.Font = ResourceLoader.Load<Font>(
				"res://demos/manosaba/assets/fonts/SourceHanSerifSC.otf");
			configuration.Presentation.FontSize = 34;
			configuration.Presentation.LettersPerSecond = 36;

			var runtime = new MajoRuntimeBootstrap
			{
				Name = "ManosabaRuntime",
				Configuration = configuration,
			};

			AddChild(runtime);
			await runtime.InitializationCompletion;

			var services = runtime.Host!.Services;
			var marker = runtime.FindChild("MenuPanel", recursive: true, owned: false);
			if (marker?.GetParent() is not ManosabaPresentation presentation)
			{
				throw new InvalidOperationException(
					"Manosaba presentation did not instantiate correctly.");
			}

			presentation.Bind(
				services.Get<IPlaybackModeManager>(),
				services.Get<IHistoryManager>(),
				services.Get<ISaveLoadOverlay>(),
				services.Get<ISettingsOverlay>());

			var dialogue = services.Get<IDialoguePlayer>();
			await dialogue.StartAsync();
		}
		catch (Exception exception)
		{
			GD.PushError($"Manosaba Demo failed: {exception}");
		}
	}
}

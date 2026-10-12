#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.YarnBridge;
using YarnSpinnerGodot;
using File = System.IO.File;
using DialogueOption = Majo.Runtime.Dialogue.DialogueOption;

namespace Majo.Demos.Recording;

/// <summary>Optional recording host; ordinary demo execution does not instantiate it.</summary>
public partial class ShowcaseRecording : Control, IDialoguePresenter
{
	private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
	public static ShowcaseRecording? Current { get; private set; }
	private readonly List<object> events = new();
	private readonly Dictionary<string, string> sources = new(StringComparer.Ordinal);
	private ScriptRecordingPanel? panel;
	private string recordingStyle = "technical";
	private double? presentationStartSeconds;
	private SubViewport? game;
	private ShowcaseSourceMap? sourceMap;
	private MajoRuntimeBootstrap? runtime;
	private global::Yarn.Dialogue? vm;
	private global::Yarn.CommandHandler? originalCommand;
	private global::Yarn.LineHandler? originalLine;
	private global::Yarn.OptionsHandler? originalOptions;
	private string target = "live2d";
	private string output = string.Empty;
	private string manifest = string.Empty;
	private string title = string.Empty;
	private int fps = 30;
	private ulong startFrame;
	private double maxSeconds;
	private bool finishing;
	private bool attached;
	private bool written;
	private int mappedEvents;
	private int unmappedEvents;
	private readonly List<string> errors = new();

	public override void _Ready()
	{
		Current = this;
		startFrame = Engine.GetProcessFrames();
		try
		{
			target = Argument("--showcase", "live2d");
			output = Argument("--recording-output", "res://artifacts/recordings/" + target);
			fps = int.Parse(Argument("--recording-fps", "30"), CultureInfo.InvariantCulture);
			maxSeconds = double.Parse(Argument("--recording-seconds", "0"), CultureInfo.InvariantCulture);
			recordingStyle = Argument("--recording-style", "technical");
			if (recordingStyle is not ("technical" or "game"))
				throw new ArgumentException("Recording style must be technical or game.");
			var (demo, label, resolution) = target switch
			{
				"adv02" => ("manosaba", "Manosaba · Adv02", new Vector2I(1920, 1080)),
				"trial00" => ("manosaba", "Manosaba · Trial00", new Vector2I(1920, 1080)),
				"ace-attorney" => ("ace-attorney", "Ace Attorney · Sprite animation", new Vector2I(960, 640)),
				"live2d" => ("live2d", "Live2D · Expressions & motion", new Vector2I(1920, 1080)),
				_ => throw new ArgumentException($"Unknown showcase '{target}'."),
			};
			title = label;
			manifest = $"res://demos/{demo}/assets/scripts/main.yarnproject";
			output = ProjectSettings.GlobalizePath(output);
			Directory.CreateDirectory(output);
			BuildLayout(resolution);
			var scenePath = demo switch
			{
				"manosaba" => "res://demos/manosaba/ManosabaDemo.tscn",
				"ace-attorney" => "res://demos/ace-attorney/AceAttorneyDemo.tscn",
				_ => "res://demos/live2d/Live2DDemo.tscn",
			};
			var scene = ResourceLoader.Load<PackedScene>(scenePath) ?? throw new IOException(scenePath);
			game!.AddChild(scene.Instantiate());
		}
		catch (Exception exception) { Fail(exception); }
	}

	public void Configure(MajoProjectConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		configuration.Diagnostics!.EnableOverlay = false;
		configuration.Diagnostics.ShowFpsOverlay = false;
		configuration.Dialogue!.AbortOnCommandError = true;
		configuration.Presentation!.Enabled = true;
		if (target is "ace-attorney" or "live2d")
			configuration.Presentation.Font ??= ResourceLoader.Load<Font>("res://demos/manosaba/assets/fonts/SourceHanSansKR.otf");
		if (target is "adv02" or "trial00")
			configuration.Dialogue.DefaultStartNode = target == "adv02" ? "Act01_Chapter01_Adv02" : "Act01_Chapter01_Trial00";
		// Keep recording settings separate from the player's saved preferences and slots.
		configuration.State!.StorageNamespace = "majo/recording/" + target;
	}

	public void Attach(MajoRuntimeBootstrap host)
	{
		runtime = host;
		var project = ResourceLoader.Load<YarnProject>(manifest) ?? throw new IOException(manifest);
		var yarnManifest = global::Yarn.Compiler.Project.LoadFromFile(ProjectSettings.GlobalizePath(manifest));
		var files = yarnManifest.SourceFiles.Select(path => (Path: path, Content: File.ReadAllText(path))).ToArray();
		foreach (var file in files) sources[file.Path] = file.Content;
		sourceMap = new ShowcaseSourceMap(files, Actions.GetLibrary(), yarnManifest.FileVersion, project.Program);
		vm = host.Host!.Services.Get<IYarnDialogueBackend>().Runner.Dialogue;
		originalCommand = vm.CommandHandler;
		originalLine = vm.LineHandler;
		originalOptions = vm.OptionsHandler;
		vm.CommandHandler = command =>
		{
			if (!command.Text.StartsWith(YarnNodeEntryTransform.CommandName, StringComparison.Ordinal))
				Observe("command", command.Text);
			originalCommand?.Invoke(command);
		};
		vm.LineHandler = line =>
		{
			Observe("dialogue", line.ID);
			originalLine?.Invoke(line);
		};
		vm.OptionsHandler = options =>
		{
			if (options.Options.Any(option => option.Line.ID.EndsWith("_replay", StringComparison.Ordinal)))
			{
				Finish("completed");
				return;
			}
			Observe("options", string.Join(", ", options.Options.Select(option => option.Line.ID)));
			originalOptions?.Invoke(options);
		};
		var player = host.Host.Services.Get<IDialoguePlayer>();
		player.Completed += _ => Finish("completed");
		player.Failed += Fail;
		var playback = host.Host.Services.Get<IPlaybackModeManager>();
		playback.ApplyAutoSettings(TimeSpan.FromSeconds(1.1), waitForVoice: true);
		playback.SetMode(PlaybackMode.Auto);
		attached = true;
		if (host.FindChild("MajoDefaultDialoguePresentation", recursive: true, owned: false) is Control presentation)
		{
			presentation.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			presentation.Size = game!.Size;
		}
		GD.Print($"SHOWCASE_READY {target}");
	}

	private void Observe(string kind, string text)
	{
		if (finishing || vm is null || sourceMap is null) return;
		var (node, instruction) = YarnVmStateBridge.GetExecutionPosition(vm);
		var position = sourceMap.Resolve(node, instruction);
		if (position is null)
		{
			unmappedEvents++;
			Fail(new InvalidOperationException($"No source mapping for {node}:{instruction} ({kind} {text})."));
			return;
		}
		mappedEvents++;
		var seconds = (Engine.GetProcessFrames() - startFrame) / (double)fps;
		if (presentationStartSeconds is null &&
			(target == "adv02" ? kind == "command" && text.StartsWith("bgm ", StringComparison.Ordinal) : kind == "dialogue"))
			presentationStartSeconds = seconds;
		panel?.ShowPosition(position.File, node, position.Line, sources[position.File]);
		events.Add(new
		{
			frame = Engine.GetProcessFrames() - startFrame,
			seconds = (Engine.GetProcessFrames() - startFrame) / (double)fps,
			kind,
			file = ProjectSettings.LocalizePath(position.File),
			node,
			line = position.Line,
			instruction,
			text,
		});
	}

	public override void _Process(double delta)
	{
		if (finishing) return;
		var seconds = (Engine.GetProcessFrames() - startFrame) / (double)fps;
		if (maxSeconds > 0 && seconds >= maxSeconds) Finish("preview");
		else if (!attached && seconds > 20) Fail(new TimeoutException("Demo initialization did not attach the recording observer."));
	}

	public Task<DialogueOption?> PresentOptionsAsync(IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(options.FirstOrDefault(option => option.IsAvailable));
	}

	public void Fail(Exception exception)
	{
		errors.Add(exception.ToString());
		GD.PushError($"SHOWCASE_FAILED {exception}");
		Finish("failed");
	}

	private async void Finish(string reason)
	{
		if (finishing) return;
		finishing = true;
		try
		{
			await ToSignal(GetTree().CreateTimer(0.75), SceneTreeTimer.SignalName.Timeout);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			if (game is not null)
			{
				using var gameImage = game.GetTexture().GetImage();
				gameImage.SavePng(Path.Combine(output, "game.png"));
				using var image = GetViewport().GetTexture().GetImage();
				image.SavePng(Path.Combine(output, "preview.png"));
			}
			if (runtime is not null) await runtime.RequestShutdownAsync();
			WriteReport(reason);
			GD.Print($"SHOWCASE_COMPLETE {target} {reason} {mappedEvents} mapped events");
			GetTree().Quit(reason == "failed" ? 1 : 0);
		}
		catch (Exception exception)
		{
			errors.Add(exception.ToString());
			WriteReport("failed");
			GD.PushError(exception.ToString());
			GetTree().Quit(1);
		}
	}

	private void WriteReport(string reason)
	{
		if (written || string.IsNullOrEmpty(output)) return;
		written = true;
		File.WriteAllText(Path.Combine(output, "timeline.json"), JsonSerializer.Serialize(new
		{
			target,
			title,
			status = reason,
			fps,
			recordingStyle,
			presentationStartSeconds,
			frames = Engine.GetProcessFrames() - startFrame,
			mappedEvents,
			unmappedEvents,
			errors,
			gameRect = new { x = target == "ace-attorney" ? 200 : 0, y = 0, width = target == "ace-attorney" ? 2160 : 2560, height = 1440 },
			events,
		}, ReportOptions));
	}

	private void BuildLayout(Vector2I resolution)
	{
		var window = GetWindow();
		window.ContentScaleSize = new Vector2I(2560, 1440);
		window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
		window.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
		var background = new ColorRect { Color = new Color("070a0f") };
		AddChild(background);
		background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		game = new SubViewport
		{
			Name = "GameViewport",
			Size = resolution,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			HandleInputLocally = true,
		};
		AddChild(game);
		var image = new TextureRect
		{
			Position = Vector2.Zero,
			Size = new Vector2(2560, 1440),
			Texture = game.GetTexture(),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		AddChild(image);
		if (recordingStyle == "technical")
		{
			panel = new ScriptRecordingPanel
			{
				Position = Vector2.Zero,
				Size = new Vector2(1440, ScriptRecordingPanel.PanelHeight),
			};
			AddChild(panel);
		}
	}

	public override void _ExitTree()
	{
		if (vm is not null)
		{
			vm.CommandHandler = originalCommand;
			vm.LineHandler = originalLine;
			vm.OptionsHandler = originalOptions;
		}
		if (!written) WriteReport("interrupted");
		if (Current == this) Current = null;
	}

	private static string Argument(string name, string fallback) => OS.GetCmdlineUserArgs()
		.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? fallback;
}

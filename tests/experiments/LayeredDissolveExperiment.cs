#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Experiments;

// Manual comparison only. Production actor code is unchanged.
public partial class LayeredDissolveExperiment : Node
{
	private const int Steps = 40;
	private readonly List<ImageTexture> frames = new();
	private readonly List<string> appearances = new();
	private SubViewport viewport = null!;
	private ICharacterActor actor = null!;
	private TextureRect reference = null!;
	private TextureRect candidate = null!;
	private ShaderMaterial material = null!;
	private HSlider progress = null!;
	private OptionButton choices = null!;
	private Label status = null!;
	private ImageTexture? sourceTexture;
	private ImageTexture? targetTexture;
	private string defaultAppearance = null!;
	private bool busy = true;
	private bool playing;
	private double playback;

	public override async void _Ready()
	{
		try
		{
			BuildUi();
			var configuration = MajoProjectConfiguration.CreateDefault();
			configuration.Resources!.RootPath = "res://demos/manosaba/assets";
			var bootstrap = new MajoRuntimeBootstrap { Configuration = configuration };
			AddChild(bootstrap);
			await bootstrap.InitializationCompletion;
			actor = (ICharacterActor)await bootstrap.Host!.Services.Get<IActorManager>()
				.GetOrCreateAsync(new ActorSpawnRequest("Ema", ActorFactoryIds.Character,
					"Default", CharacterPositionPreset.Center));
			viewport = new SubViewport
			{
				Size = new Vector2I(800, 1000),
				TransparentBg = true,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Always
			};
			AddChild(viewport);
			actor.View.Reparent(viewport);
			defaultAppearance = actor.Appearance;
			var layers = actor.View.GetNode<CanvasGroup>("LayerComposite").GetChildren().OfType<Sprite2D>().ToArray();
			var bounds = layers.Select(layer => layer.Transform * layer.GetRect()).Aggregate((a, b) => a.Merge(b));
			var scale = Math.Min(760f / bounds.Size.X, 960f / bounds.Size.Y);
			actor.SetScale(new Vector2(scale, scale));
			actor.SetPosition(new Vector2(400, 500) - bounds.GetCenter() * scale);
			actor.SetVisible(true);
			var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(
				ProjectSettings.GlobalizePath("res://demos/manosaba/assets/characters/Ema/layered_character.json")));
			foreach (var item in json.RootElement.GetProperty("compositionMap").EnumerateObject())
			{ appearances.Add(item.Name); choices.AddItem(item.Name); }
			json.Dispose();
			var initial = appearances.IndexOf("Surprised1");
			choices.Select(initial);
			await PrepareAsync(appearances[initial]);
		}
		catch (Exception exception) { Fail(exception); }
	}

	private void BuildUi()
	{
		// CanvasLayer keeps the experiment UI above the bootstrap's legacy stage.
		var ui = new CanvasLayer { Layer = 1000 }; AddChild(ui);
		var backdrop = new ColorRect { Color = new Color(0.2f, 0.22f, 0.25f) };
		backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); ui.AddChild(backdrop);
		var box = new VBoxContainer(); box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		box.OffsetLeft = 20; box.OffsetRight = -20; box.OffsetTop = 16; box.OffsetBottom = -16; ui.AddChild(box);
		box.AddChild(new Label { Text = "Layered appearance / Whole-image dissolve — Ema", HorizontalAlignment = HorizontalAlignment.Center });
		box.AddChild(new Label { Text = "LEFT: production ChangeAppearance   |   RIGHT: two captured endpoints blended by one shader", HorizontalAlignment = HorizontalAlignment.Center });
		var controls = new HBoxContainer(); box.AddChild(controls);
		choices = new OptionButton { Disabled = true }; controls.AddChild(choices);
		choices.ItemSelected += async index => { try { await PrepareAsync(appearances[(int)index]); } catch (Exception e) { Fail(e); } };
		var play = new Button { Text = "Play / Pause (3 seconds)" }; controls.AddChild(play);
		play.Pressed += () => { if (busy) return; playing = !playing; if (progress.Value >= 1) progress.Value = 0; playback = progress.Value; };
		var background = new OptionButton();
		foreach (var name in new[] { "Gray background", "White background", "Black background" }) background.AddItem(name);
		controls.AddChild(background);
		background.ItemSelected += index => backdrop.Color = index switch { 1 => Colors.White, 2 => Colors.Black, _ => new Color(0.2f, 0.22f, 0.25f) };
		progress = new HSlider { MinValue = 0, MaxValue = 1, Step = 1d / Steps, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		box.AddChild(progress); progress.ValueChanged += value => ShowProgress((float)value);
		status = new Label { Text = "Loading production actor..." }; box.AddChild(status);
		var pair = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; box.AddChild(pair);
		reference = MakePanel(); candidate = MakePanel(); pair.AddChild(reference); pair.AddChild(candidate);
		material = new ShaderMaterial { Shader = new Shader { Code = @"
            shader_type canvas_item;
            render_mode unshaded;
            uniform sampler2D source_image : filter_linear;
            uniform sampler2D target_image : filter_linear;
            uniform float progress;
            void fragment() {
                // Transparent viewport captures contain premultiplied RGB.
                vec4 c = mix(texture(source_image, UV), texture(target_image, UV), progress);
                if (c.a > 0.00001) c.rgb /= c.a;
                else c.rgb = vec3(0.0);
                COLOR = c;
            }
			" } };
		candidate.Material = material;
		// Display the production capture with the same unpremultiply conversion.
		reference.Material = new ShaderMaterial { Shader = new Shader { Code = @"
            shader_type canvas_item;
            render_mode unshaded;
            void fragment() {
                vec4 c = texture(TEXTURE, UV);
                if (c.a > 0.00001) c.rgb /= c.a;
                else c.rgb = vec3(0.0);
                COLOR = c;
            }
			" } };
	}

	private static TextureRect MakePanel() => new()
	{
		ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
		SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		SizeFlagsVertical = Control.SizeFlags.ExpandFill,
	};

	private async Task<ImageTexture> CaptureAsync()
	{
		for (var i = 0; i < 2; i++)
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = viewport.GetTexture().GetImage();
		return ImageTexture.CreateFromImage(image);
	}

	private async Task PrepareAsync(string target)
	{
		busy = true; playing = false; choices.Disabled = true;
		status.Text = $"Sampling actual ChangeAppearance: Default → {target}...";
		reference.Texture = null; candidate.Texture = null;
		foreach (var frame in frames) frame.Dispose(); frames.Clear();
		sourceTexture?.Dispose(); targetTexture?.Dispose();
		actor.SetAppearance(defaultAppearance);
		var source = actor.Appearance;
		sourceTexture = await CaptureAsync();
		actor.SetAppearance(target);
		var destination = actor.Appearance;
		targetTexture = await CaptureAsync();
		actor.SetAppearance(source);
		var previous = GetTree().GetProcessedTweens().ToHashSet();
		var operation = actor.ChangeAppearance(destination, 1);
		var tweens = GetTree().GetProcessedTweens().Where(tween => !previous.Contains(tween)).ToArray();
		foreach (var tween in tweens) tween.Pause();
		for (var step = 0; step <= Steps; step++)
		{
			foreach (var tween in tweens) if (tween.IsValid()) tween.CustomStep(step == 0 ? 0 : 1d / Steps);
			frames.Add(await CaptureAsync());
		}
		operation.Finish(); await operation.Completion;
		material.SetShaderParameter("source_image", sourceTexture);
		material.SetShaderParameter("target_image", targetTexture);
		candidate.Texture = sourceTexture;
		busy = false; choices.Disabled = false; progress.Value = 0.5;
		ShowProgress((float)progress.Value);
		GD.Print($"DISSOLVE_EXPERIMENT_READY {target}: {frames.Count} production frames, {tweens.Length} layer tweens");
		if (OS.GetCmdlineUserArgs().Contains("--experiment-smoke"))
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var screenshot = GetViewport().GetTexture().GetImage();
			screenshot.SavePng("/tmp/majo-dissolve-experiment.png");
			GetTree().Quit();
		}
	}

	private void ShowProgress(float value)
	{
		if (busy || frames.Count == 0) return;
		reference.Texture = frames[Math.Clamp((int)Math.Round(value * Steps), 0, Steps)];
		material.SetShaderParameter("progress", value);
		status.Text = $"Default → {choices.GetItemText(choices.Selected)}    progress: {value:0.000}    (same framing / endpoint captures; linear easing)";
	}

	public override void _Process(double delta)
	{
		if (busy || !playing) return;
		playback += delta / 3;
		progress.Value = Math.Min(playback, 1);
		if (playback >= 1) playing = false;
	}

	private void Fail(Exception exception)
	{
		GD.PrintErr(exception); status.Text = exception.Message; playing = false;
		if (OS.GetCmdlineUserArgs().Contains("--experiment-smoke")) GetTree().Quit(1);
	}
}

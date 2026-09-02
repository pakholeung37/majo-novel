#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class DirectionCommandModule : IRuntimeCommandModule
{
	private readonly IDirectionBeatRuntime beats;
	private readonly IDirectionResourceKeyResolver resources;
	private readonly IDirectionPositionResolver positions;
	private readonly IDirectionScopeResolver scopes;
	private readonly Func<string?> currentNode;
	private long anonymousBeatSequence;

	public DirectionCommandModule(
		IDirectionBeatRuntime beats,
		IDirectionResourceKeyResolver resources,
		IDirectionPositionResolver positions,
		IDirectionScopeResolver scopes,
		Func<string?> currentNode)
	{
		this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
		this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
		this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
		this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
		this.currentNode = currentNode ?? throw new ArgumentNullException(nameof(currentNode));
	}

	public void Register(IRuntimeCommandRegistrar commands)
	{
		ArgumentNullException.ThrowIfNull(commands);
		commands
			.Register<BackdropDirectionCommandArguments>(BackdropAsync)
			.Register<CharacterDirectionCommandArguments>(CharacterAsync)
			.Register<LeaveDirectionCommandArguments>(LeaveAsync)
			.Register<CgDirectionCommandArguments>(CgAsync)
			.Register<CameraPoseDirectionCommandArguments>(CameraPoseAsync)
			.Register<CameraMoveDirectionCommandArguments>(CameraMoveAsync)
			.Register<CameraResetDirectionCommandArguments>(CameraResetAsync)
			.Register<CameraShakeDirectionCommandArguments>(CameraShakeAsync)
			.Register<ShotUseDirectionCommandArguments>(ShotUseAsync)
			.Register<LayoutDirectionCommandArguments>(LayoutAsync)
			.Register<DirectionImportCommandArguments>(ImportAsync)
			.Register<DirectionMarkCommandArguments>(MarkAsync)
			.Register<FlashDirectionCommandArguments>(FlashAsync)
			.Register<CurtainDirectionCommandArguments>(CurtainAsync)
			.Register<BeatBeginDirectionCommandArguments>(BeatBeginAsync)
			.Register<BeatEndDirectionCommandArguments>(BeatEndAsync)
			.Register<BeatJoinDirectionCommandArguments>(BeatJoinAsync)
			.Register<BeatFinishDirectionCommandArguments>(BeatFinishAsync)
			.Register<BeatAbortDirectionCommandArguments>(BeatAbortAsync);
	}

	private Task BackdropAsync(
		BackdropDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("backdrop", async () =>
	{
		await beats.SubmitAsync(
			new BackdropPatch(
				Stage(a.Stage),
				new BackdropState(resources.ResolveBackdrop(a.Resource)),
				Transition(
					a,
					c,
					DirectionTransitionKind.Cut,
					DirectionTransitionKind.Fade,
					DirectionTransitionKind.Dissolve)),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task CharacterAsync(
		CharacterDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("character", async () =>
	{
		await beats.SubmitAsync(
			new ActorPatch(
				Stage(a.Stage),
				new ActorId(a.Actor),
				a.At is null
					? default
					: DirectionValue<DirectionPoint>.From(ResolvePosition(a.At)),
				a.Appearance is null
					? default
					: DirectionValue<string?>.From(a.Appearance),
				a.Scale is null
					? default
					: DirectionValue<float>.From((float)a.Scale.Value),
				a.Z is null ? default : DirectionValue<int>.From(a.Z.Value),
				Transition(
					a,
					c,
					DirectionTransitionKind.Cut,
					DirectionTransitionKind.Move,
					DirectionTransitionKind.Fade)),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task LeaveAsync(
		LeaveDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("leave", async () =>
	{
		await beats.SubmitAsync(
			new ActorLeavePatch(
				Stage(a.Stage),
				new ActorId(a.Actor),
				Transition(
					a,
					c,
					DirectionTransitionKind.Cut,
					DirectionTransitionKind.Fade)),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task CameraPoseAsync(
		CameraPoseDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("camera pose", async () =>
	{
		var patch = CameraPatch(a, CameraPoseTransition(a, a.Via, c));
		if (a.As is not null)
			scopes.DefineShot(RequireNode(), a.As, patch);
		await beats.SubmitAsync(patch, Barrier(a.Wait), c.CancellationToken);
	});

	private Task CameraMoveAsync(
		CameraMoveDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("camera move", async () =>
	{
		var transition = MotionTransition(a, c, DirectionTransitionKind.Move);
		await beats.SubmitAsync(
			CameraPatch(a, transition),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task CameraResetAsync(
		CameraResetDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("camera reset", async () =>
	{
		var patch = new ViewPatch(
			View(a.View),
			default,
			DirectionValue<DirectionPoint>.From(new DirectionPoint(50, 50)),
			DirectionValue<float>.From(1),
			DirectionValue<float>.From(0),
			CameraPoseTransition(a, a.Via, c));
		await beats.SubmitAsync(patch, Barrier(a.Wait), c.CancellationToken);
	});

	private Task CameraShakeAsync(
		CameraShakeDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("camera shake", async () =>
	{
		await beats.SubmitAsync(
			new ShakeCue(
				(float)a.Strength,
				a.Axis,
				a.Frequency,
				a.Duration,
				new DirectionSourceLocation(RequireNode(), 0, 0)),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task ShotUseAsync(
		ShotUseDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("shot use", async () =>
	{
		var patch = scopes.ResolveShot(RequireNode(), a.Alias);
		if (a.View is not null)
			patch = patch with { ViewId = View(a.View) };
		patch = patch with { Transition = Transition(a, c, Enum.GetValues<DirectionTransitionKind>()) };
		await beats.SubmitAsync(patch, Barrier(a.Wait), c.CancellationToken);
	});

	private Task CgAsync(
		CgDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("cg", async () =>
	{
		await beats.SubmitAsync(
			new ViewPatch(
				View(a.View),
				DirectionValue<ViewSource>.From(
					new CgViewSource(resources.ResolveCg(a.Resource))),
				a.Center is null
					? default
					: DirectionValue<DirectionPoint>.From(
						new DirectionPoint(a.Center.Value.X, a.Center.Value.Y)),
				a.Zoom is null
					? default
					: DirectionValue<float>.From((float)a.Zoom.Value),
				a.Rotation is null
					? default
					: DirectionValue<float>.From((float)a.Rotation.Value),
				Transition(a, c, Enum.GetValues<DirectionTransitionKind>())),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task LayoutAsync(
		LayoutDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("layout", async () =>
	{
		var bindings = new Dictionary<LayoutSlotId, ViewId>();
		foreach (var pair in a.Bindings)
			bindings.Add(new LayoutSlotId(pair.Key), View(pair.Value));
		await beats.SubmitAsync(
			new ScreenPatch(
				new LayoutId(a.Layout),
				bindings,
				Transition(a, c, DirectionTransitionKind.Cut, DirectionTransitionKind.Dissolve)),
			Barrier(a.Wait),
			c.CancellationToken);
	});

	private Task ImportAsync(DirectionImportCommandArguments a, RuntimeCommandContext _) => ExecuteAsync("import", () =>
	{
		var node = RequireNode();
		if (a.Kind.Equals("stage", StringComparison.OrdinalIgnoreCase))
			scopes.ImportStage(node, a.Alias, a.SourceNode, a.SourceSymbol);
		else if (a.Kind.Equals("view", StringComparison.OrdinalIgnoreCase))
			scopes.ImportView(node, a.Alias, a.SourceNode, a.SourceSymbol);
		else throw new DirectionReferenceException($"Unsupported import kind '{a.Kind}'.");
		return Task.CompletedTask;
	});

	private Task MarkAsync(DirectionMarkCommandArguments a, RuntimeCommandContext _) => ExecuteAsync("mark", () =>
	{
		var node = RequireNode();
		var position = positions.Resolve(a.At);
		try
		{
			scopes.DefineMark(node, a.Name, position);
		}
		catch (DirectionReferenceException)
		{
			if (!scopes.ResolveMark(node, a.Name).Equals(position))
				throw;
		}
		return Task.CompletedTask;
	});

	private Task FlashAsync(FlashDirectionCommandArguments a, RuntimeCommandContext c) =>
		ExecuteAsync("flash", async () =>
		{
			await beats.SubmitAsync(
				new FlashCue(
					new DirectionColor((float)a.R, (float)a.G, (float)a.B),
					(float)a.Intensity,
					a.Duration,
					new DirectionSourceLocation(RequireNode(), 0, 0)),
				Barrier(a.Wait),
				c.CancellationToken);
		});

	private Task CurtainAsync(CurtainDirectionCommandArguments a, RuntimeCommandContext c) =>
		ExecuteAsync("curtain", async () =>
		{
			await beats.SubmitAsync(
				new CurtainCue(
					a.Position == StageCurtainPosition.Open,
					new DirectionColor((float)a.R, (float)a.G, (float)a.B),
					a.Duration,
					new DirectionSourceLocation(RequireNode(), 0, 0)),
				Barrier(a.Wait),
				c.CancellationToken);
		});

	private DirectionPoint ResolvePosition(string value)
	{
		try { return scopes.ResolveMark(RequireNode(), value); }
		catch (DirectionReferenceException) { return positions.Resolve(value); }
	}

	private Task BeatBeginAsync(
		BeatBeginDirectionCommandArguments a,
		RuntimeCommandContext _) => ExecuteAsync("beat begin", () =>
	{
		var node = currentNode();
		if (string.IsNullOrWhiteSpace(node))
			throw new DirectionBeatException("Cannot begin a Beat outside a Yarn Node.");
		var beatId = string.IsNullOrWhiteSpace(a.Id)
			? $"{node.Trim()}#anonymous-{Interlocked.Increment(ref anonymousBeatSequence)}"
			: a.Id;
		beats.Begin(
			beatId,
			node,
			new DirectionBeatDefaults(a.Duration, ParseEase(a.Ease)));
		return Task.CompletedTask;
	});

	private Task BeatEndAsync(
		BeatEndDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("beat end", async () =>
	{
		await beats.EndAsync(Barrier(a.Wait), c.CancellationToken);
	});

	private Task BeatJoinAsync(
		BeatJoinDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("beat join", () =>
		beats.JoinAsync(a.Id, c.CancellationToken));

	private Task BeatFinishAsync(
		BeatFinishDirectionCommandArguments a,
		RuntimeCommandContext c) => ExecuteAsync("beat finish", () =>
		beats.FinishAsync(a.Id, c.CancellationToken));

	private Task BeatAbortAsync(
		BeatAbortDirectionCommandArguments _,
		RuntimeCommandContext __) => ExecuteAsync("beat abort", () =>
	{
		beats.AbortOpen();
		return Task.CompletedTask;
	});

	private DirectionTransitionSpec Transition(
		DirectionTransitionCommandArguments a,
		RuntimeCommandContext context,
		params DirectionTransitionKind[] allowed)
	{
		var kind = a.Via ?? DirectionTransitionKind.Cut;
		if (Array.IndexOf(allowed, kind) < 0)
		{
			throw new DirectionException(
				$"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by this command.");
		}
		var defaults = beats.OpenBeatDefaults;
		var duration = a.Duration ?? (kind == DirectionTransitionKind.Cut
			? 0
			: defaults?.DurationSeconds ??
				context.Binding?.DefaultAnimationDurationSeconds ?? 0);
		var ease = ParseEase(a.Ease) ?? defaults?.Ease ?? DirectionEase.Linear;
		return new DirectionTransitionSpec(kind, duration, ease).Validate();
	}

	private DirectionTransitionSpec MotionTransition(
		DirectionTimingCommandArguments a,
		RuntimeCommandContext context,
		DirectionTransitionKind kind)
	{
		var defaults = beats.OpenBeatDefaults;
		var duration = a.Duration ?? defaults?.DurationSeconds ??
			context.Binding?.DefaultAnimationDurationSeconds ?? 0;
		var ease = ParseEase(a.Ease) ?? defaults?.Ease ?? DirectionEase.Linear;
		return new DirectionTransitionSpec(kind, duration, ease).Validate();
	}

	private DirectionTransitionSpec CameraPoseTransition(
		DirectionTimingCommandArguments a,
		DirectionTransitionKind? via,
		RuntimeCommandContext context)
	{
		// A pose command with an explicit duration but no `via` is the common
		// camera-pan shorthand. The cut remains the default when no duration
		// is supplied, while `via:cut` still requires duration:0.
		var kind = via ?? (a.Duration.HasValue
			? DirectionTransitionKind.Move
			: DirectionTransitionKind.Cut);
		var allowed = Enum.GetValues<DirectionTransitionKind>();
		if (Array.IndexOf(allowed, kind) < 0)
			throw new DirectionException(
				$"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by this command.");
		var defaults = beats.OpenBeatDefaults;
		var duration = a.Duration ?? (kind == DirectionTransitionKind.Cut
			? 0
			: defaults?.DurationSeconds ??
				context.Binding?.DefaultAnimationDurationSeconds ?? 0);
		var ease = ParseEase(a.Ease) ?? defaults?.Ease ?? DirectionEase.Linear;
		return new DirectionTransitionSpec(kind, duration, ease).Validate();
	}

	private ViewPatch CameraPatch(
		CameraPatchDirectionCommandArguments a,
		DirectionTransitionSpec transition)
	{
		var stage = (a as CameraPoseDirectionCommandArguments)?.Stage;
		return new ViewPatch(
		View(a.View),
		stage is null
			? default
			: DirectionValue<ViewSource>.From(new StageViewSource(Stage(stage))),
		a.Center is null
			? default
			: DirectionValue<DirectionPoint>.From(
				new DirectionPoint(a.Center.Value.X, a.Center.Value.Y)),
		a.Zoom is null
			? default
			: DirectionValue<float>.From((float)a.Zoom.Value),
		a.Rotation is null
			? default
			: DirectionValue<float>.From((float)a.Rotation.Value),
		transition);
	}

	private static DirectionEase? ParseEase(string? value)
	{
		if (value is null) return null;
		return value.Trim().ToLowerInvariant() switch
		{
			"linear" => DirectionEase.Linear,
			"sine-in" or "sinein" => DirectionEase.SineIn,
			"sine-out" or "sineout" => DirectionEase.SineOut,
			"sine-in-out" or "sineinout" => DirectionEase.SineInOut,
			"cubic-in" or "cubicin" => DirectionEase.CubicIn,
			"cubic-out" or "cubicout" => DirectionEase.CubicOut,
			"cubic-in-out" or "cubicinout" => DirectionEase.CubicInOut,
			"back-out" or "backout" => DirectionEase.BackOut,
			"back-in" or "backin" => DirectionEase.BackIn,
			"back-in-out" or "backinout" => DirectionEase.BackInOut,
			_ => throw new ArgumentException(
				$"Unsupported Direction ease '{value}'."),
		};
	}

	private static DirectionCompletionBarrier Barrier(bool wait) => wait
		? DirectionCompletionBarrier.Settle
		: DirectionCompletionBarrier.Commit;

	private StageId Stage(string value) => scopes.ResolveStage(RequireNode(), value);

	private ViewId View(string value) => scopes.ResolveView(RequireNode(), value);

	private string RequireNode()
	{
		var node = currentNode();
		if (string.IsNullOrWhiteSpace(node))
			throw new DirectionReferenceException(
				"Direction symbols cannot be resolved outside a Yarn Node.");
		return node;
	}

	private static async Task ExecuteAsync(string command, Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (RuntimeCommandException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is DirectionException or ArgumentException)
		{
			throw new RuntimeCommandException(command, exception.Message, exception);
		}
	}
}

[RuntimeCommand("import", Category = "Direction", Usage = "import <stage> alias:<alias> source:<node:symbol>")]
public sealed class DirectionImportCommandArguments
{
	[CommandParameter("kind", 0, Primary = true, Required = true)] public string Kind { get; set; } = "stage";
	[CommandParameter("alias", 1, Primary = false, Required = true)] public string Alias { get; set; } = string.Empty;
	[CommandParameter("source", 2, Required = true)] public string Source { get; set; } = string.Empty;
	public string SourceNode => Source.Split(':', 2)[0];
	public string SourceSymbol => Source.Split(':', 2).Length == 2 ? Source.Split(':', 2)[1] : string.Empty;
}

[RuntimeCommand("mark", Category = "Direction", Usage = "mark <name> at:<mark>")]
public sealed class DirectionMarkCommandArguments
{
	[CommandParameter("name", 0, Primary = true, Required = true)] public string Name { get; set; } = string.Empty;
	[CommandParameter("at", 1, Required = true)] public string At { get; set; } = string.Empty;
}

[RuntimeCommand("camera", "shake", Category = "Direction", Usage = "camera shake [strength:<number>] [axis:<all|x|y|diagonal>] [frequency:<number>] [duration:<seconds>] [wait:<bool>]")]
public sealed class CameraShakeDirectionCommandArguments
{
	[CommandParameter("strength", 0, DefaultValue = "8", Minimum = 0)] public double Strength { get; set; }
	[CommandParameter("axis", 1, DefaultValue = "all")] public CameraShakeAxis Axis { get; set; }
	[CommandParameter("frequency", 2, DefaultValue = "20", Minimum = 0.001)] public int Frequency { get; set; }
	[CommandParameter("duration", 3, DefaultValue = "0.4", Minimum = 0)] public double Duration { get; set; }
	[CommandParameter("wait", 4, DefaultValue = "false")] public bool Wait { get; set; }
}

[RuntimeCommand("flash", Category = "Direction", Usage = "flash [intensity:<0..1>] [duration:<seconds>] [r:<0..1>] [g:<0..1>] [b:<0..1>] [wait:<bool>]")]
public sealed class FlashDirectionCommandArguments
{
	[CommandParameter("intensity", 0, DefaultValue = "1", Minimum = 0)] public double Intensity { get; set; }
	[CommandParameter("duration", 1, DefaultValue = "0.15", Minimum = 0)] public double Duration { get; set; }
	[CommandParameter("r", 2, DefaultValue = "1", Minimum = 0)] public double R { get; set; }
	[CommandParameter("g", 3, DefaultValue = "1", Minimum = 0)] public double G { get; set; }
	[CommandParameter("b", 4, DefaultValue = "1", Minimum = 0)] public double B { get; set; }
	[CommandParameter("wait", 5, DefaultValue = "false")] public bool Wait { get; set; }
}

[RuntimeCommand("curtain", Category = "Direction", Usage = "curtain <open|closed> [duration:<seconds>] [r:<0..1>] [g:<0..1>] [b:<0..1>] [wait:<bool>]")]
public sealed class CurtainDirectionCommandArguments
{
	[CommandParameter("state", 0, Primary = true, Required = true)] public StageCurtainPosition Position { get; set; }
	[CommandParameter("duration", 1, DefaultValue = "0.4", Minimum = 0)] public double Duration { get; set; }
	[CommandParameter("r", 2, DefaultValue = "0", Minimum = 0)] public double R { get; set; }
	[CommandParameter("g", 3, DefaultValue = "0", Minimum = 0)] public double G { get; set; }
	[CommandParameter("b", 4, DefaultValue = "0", Minimum = 0)] public double B { get; set; }
	[CommandParameter("wait", 5, DefaultValue = "false")] public bool Wait { get; set; }
}

public abstract class DirectionTimingCommandArguments
{
	[CommandParameter("duration", 21, Minimum = 0)] public double? Duration { get; set; }
	[CommandParameter("ease", 22)] public string? Ease { get; set; }
	[CommandParameter("wait", 23, DefaultValue = "false")]
	public bool Wait { get; set; }
}

public abstract class DirectionTransitionCommandArguments : DirectionTimingCommandArguments
{
	[CommandParameter("via", 20)] public DirectionTransitionKind? Via { get; set; }
}

[RuntimeCommand("backdrop", Category = "Direction", Usage = "backdrop <resource> [stage:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class BackdropDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("resource", 0, Primary = true, Required = true)]
	public string Resource { get; set; } = string.Empty;
	[CommandParameter("stage", 1, DefaultValue = "main")]
	public string Stage { get; set; } = "main";
}

[RuntimeCommand("character", Category = "Direction", Usage = "character <id> [stage:<id>] [appearance:<id>] [at:<mark|x,y>] [scale:<number>] [z:<integer>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "appearance", "at", "scale", "z")]
public sealed class CharacterDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("actor", 0, Primary = true, Required = true)]
	public string Actor { get; set; } = string.Empty;
	[CommandParameter("stage", 1, DefaultValue = "main")]
	public string Stage { get; set; } = "main";
	[CommandParameter("appearance", 2)] public string? Appearance { get; set; }
	[CommandParameter("at", 3)] public string? At { get; set; }
	[CommandParameter("scale", 4, Minimum = 0.001)] public double? Scale { get; set; }
	[CommandParameter("z", 5)] public int? Z { get; set; }
}

[RuntimeCommand("leave", Category = "Direction", Usage = "leave <character> [stage:<id>] [via:<cut|fade>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class LeaveDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("actor", 0, Primary = true, Required = true)]
	public string Actor { get; set; } = string.Empty;
	[CommandParameter("stage", 1, DefaultValue = "main")]
	public string Stage { get; set; } = "main";
}

public abstract class CameraPatchDirectionCommandArguments : DirectionTimingCommandArguments
{
	[CommandParameter("view", 0, DefaultValue = "main")]
	public string View { get; set; } = "main";
	[CommandParameter("center", 1)] public Vector2? Center { get; set; }
	[CommandParameter("zoom", 2, Minimum = 0.001)] public double? Zoom { get; set; }
	[CommandParameter("rotation", 3)] public double? Rotation { get; set; }
}

[RuntimeCommand("camera", "pose", Category = "Direction", Usage = "camera pose [stage:<id>] [view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [as:<shot>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "stage", "center", "zoom", "rotation", "as")]
public sealed class CameraPoseDirectionCommandArguments : CameraPatchDirectionCommandArguments
{
	[CommandParameter("stage", 4)] public string? Stage { get; set; }
	[CommandParameter("as", 5)] public string? As { get; set; }
	[CommandParameter("via", 20)] public DirectionTransitionKind? Via { get; set; }
}

[RuntimeCommand("camera", "move", Category = "Direction", Usage = "camera move [view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "center", "zoom", "rotation")]
public sealed class CameraMoveDirectionCommandArguments : CameraPatchDirectionCommandArguments;

[RuntimeCommand("camera", "reset", Category = "Direction", Usage = "camera reset [view:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class CameraResetDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("view", 0, DefaultValue = "main")] public string View { get; set; } = "main";
}

[RuntimeCommand("cg", Category = "Direction", Usage = "cg <resource> [view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class CgDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("resource", 0, Primary = true, Required = true)]
	public string Resource { get; set; } = string.Empty;
	[CommandParameter("view", 1, DefaultValue = "main")]
	public string View { get; set; } = "main";
	[CommandParameter("center", 2)] public Vector2? Center { get; set; }
	[CommandParameter("zoom", 3, Minimum = 0.001)] public double? Zoom { get; set; }
	[CommandParameter("rotation", 4)] public double? Rotation { get; set; }
}

[RuntimeCommand("shot", "use", Category = "Direction", Usage = "shot use <alias> [view:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class ShotUseDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("alias", 0, Primary = true, Required = true)]
	public string Alias { get; set; } = string.Empty;
	[CommandParameter("view", 1)] public string? View { get; set; }
}

[RuntimeCommand("layout", Category = "Direction", Usage = "layout <single|split|pip> <slot:view> ... [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")]
public sealed class LayoutDirectionCommandArguments : DirectionTransitionCommandArguments
{
	[CommandParameter("layout", 0, Primary = true, Required = true)]
	public string Layout { get; set; } = "single";

	[CommandParameter("main", 1)] public string? Main { get; set; }
	[CommandParameter("inset", 2)] public string? Inset { get; set; }

	public IReadOnlyDictionary<string, string> Bindings
	{
		get
		{
			var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["main"] = Main ?? "main",
			};
			if (Inset is not null) bindings["inset"] = Inset;
			return bindings;
		}
	}
}

[RuntimeCommand("beat", "begin", Category = "Direction", Usage = "beat begin [id:<id>] [duration:<seconds>] [ease:<curve>]")]
public sealed class BeatBeginDirectionCommandArguments
{
	[CommandParameter("id", 0)] public string? Id { get; set; }
	[CommandParameter("duration", 1, Minimum = 0)] public double? Duration { get; set; }
	[CommandParameter("ease", 2)] public string? Ease { get; set; }
}

[RuntimeCommand("beat", "end", Category = "Direction", Usage = "beat end [wait:<bool>]")]
public sealed class BeatEndDirectionCommandArguments
{
	[CommandParameter("wait", 0, DefaultValue = "true")]
	public bool Wait { get; set; }
}

[RuntimeCommand("beat", "join", Category = "Direction", Usage = "beat join <id>")]
public sealed class BeatJoinDirectionCommandArguments
{
	[CommandParameter("id", 0, Primary = true, Required = true)]
	public string Id { get; set; } = string.Empty;
}

[RuntimeCommand("beat", "finish", Category = "Direction", Usage = "beat finish <id>")]
public sealed class BeatFinishDirectionCommandArguments
{
	[CommandParameter("id", 0, Primary = true, Required = true)]
	public string Id { get; set; } = string.Empty;
}

[RuntimeCommand("beat", "abort", Category = "Direction", Usage = "beat abort")]
public sealed class BeatAbortDirectionCommandArguments;

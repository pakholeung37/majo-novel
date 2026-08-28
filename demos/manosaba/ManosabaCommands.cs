#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Demos.Manosaba;

[RuntimeCommand(
	"adv_frame",
	Category = "Manosaba",
	Usage = "adv_frame <default|stream> [duration:<seconds>]")]
public sealed class ManosabaAdvFrameCommandArguments
{
	[CommandParameter("mode", 0, Primary = true, Required = true, ValueHint = "default|stream")]
	public string Mode { get; set; } = "default";

	[CommandParameter("duration", 1, ValueHint = "seconds", Minimum = 0)]
	public double Duration { get; set; }
}

[RuntimeCommand(
	"stream_frame",
	Category = "Manosaba",
	Usage = "stream_frame <enabled:bool> [duration:<seconds>]")]
public sealed class ManosabaStreamFrameCommandArguments
{
	[CommandParameter("enabled", 0, Primary = true, Required = true, ValueHint = "bool")]
	public bool Enabled { get; set; } = true;

	[CommandParameter("duration", 1, ValueHint = "seconds", Minimum = 0)]
	public double Duration { get; set; }
}

public static class ManosabaCommands
{
	public static RuntimeCommandRegistry Register(RuntimeCommandRegistry commands)
	{
		ArgumentNullException.ThrowIfNull(commands);
		commands.Register<ManosabaAdvFrameCommandArguments>(HandleAdvFrameAsync);
		commands.Register<ManosabaStreamFrameCommandArguments>(HandleStreamFrameAsync);
		return commands;
	}

	private static Task HandleAdvFrameAsync(
		ManosabaAdvFrameCommandArguments arguments,
		RuntimeCommandContext context)
	{
		var services = context.Require(context.Services, "ServiceResolver");
		var ui = services.Get<IManosabaUiService>();
		var mode = ParseMode(arguments.Mode);
		ui.SetFrameMode(mode, arguments.Duration);
		return Task.CompletedTask;
	}

	private static Task HandleStreamFrameAsync(
		ManosabaStreamFrameCommandArguments arguments,
		RuntimeCommandContext context)
	{
		var services = context.Require(context.Services, "ServiceResolver");
		var ui = services.Get<IManosabaUiService>();
		var mode = arguments.Enabled ? ManosabaFrameMode.Stream : ManosabaFrameMode.Default;
		ui.SetFrameMode(mode, arguments.Duration);
		return Task.CompletedTask;
	}

	public static ManosabaFrameMode ParseMode(string rawMode)
	{
		if (string.IsNullOrWhiteSpace(rawMode))
		{
			return ManosabaFrameMode.Default;
		}

		return rawMode.Trim().ToLowerInvariant() switch
		{
			"stream" or "streamframe" or "tv" or "broadcast" or "true" or "on" => ManosabaFrameMode.Stream,
			"default" or "normal" or "adv" or "false" or "off" => ManosabaFrameMode.Default,
			_ => throw new RuntimeCommandException(
				"adv_frame",
				$"Unknown frame mode '{rawMode}'. Expected 'default' or 'stream'.")
		};
	}
}

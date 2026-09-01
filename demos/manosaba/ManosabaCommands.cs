#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Demos.Manosaba;

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
		commands.Register<ManosabaStreamFrameCommandArguments>(HandleStreamFrameAsync);
		return commands;
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

}

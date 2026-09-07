#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Demos.Manosaba;

[Command("stream_frame", Category = "Manosaba")]
public sealed partial class ManosabaStreamFrameCommand : RuntimeCommand<ManosabaStreamFrameCommand.Arguments>
{
    private readonly IManosabaUiService ui;

    public ManosabaStreamFrameCommand(IManosabaUiService ui)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var mode = arguments.Enabled ? ManosabaFrameMode.Stream : ManosabaFrameMode.Default;
        ui.SetFrameMode(mode, arguments.Duration.GetValueOrDefault(0.0));
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments(
        [Primary(ValueHint = "bool")] bool Enabled,
        [Option(ValueHint = "seconds", Minimum = 0)] CommandValue<double> Duration);
}

public static class ManosabaCommands
{
    public static IRuntimeCommandRegistrar Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Add(
            "stream_frame",
            services => new ManosabaStreamFrameCommand(services.Get<IManosabaUiService>()),
            "manosaba",
            "1.0");
        return commands;
    }
}

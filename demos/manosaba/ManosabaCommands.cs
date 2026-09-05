#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Demos.Manosaba;

public sealed class ManosabaStreamFrameCommand : RuntimeCommand<ManosabaStreamFrameCommand.Arguments>
{
    private readonly IManosabaUiService ui;

    public ManosabaStreamFrameCommand(IManosabaUiService ui)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "stream_frame",
                category: "Manosaba",
                usage: "stream_frame <enabled:bool> [duration:<seconds>]")
            .Primary<bool>("enabled", valueHint: "bool")
            .Optional<double>("duration", valueHint: "seconds", minimum: 0)
            .Build(static values => new Arguments(
                values.Required<bool>("enabled"),
                values.Optional<double>("duration").GetValueOrDefault(0.0)));

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var mode = arguments.Enabled ? ManosabaFrameMode.Stream : ManosabaFrameMode.Default;
        ui.SetFrameMode(mode, arguments.Duration);
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments(
        bool Enabled,
        double Duration);
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

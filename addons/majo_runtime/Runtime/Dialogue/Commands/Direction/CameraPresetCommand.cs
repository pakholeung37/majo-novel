#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Dialogue.Commands;

[Command("camera preset define", Category = "Direction")]
public sealed partial class CameraPresetDefineCommand : RuntimeCommand<CameraPresetDefineCommand.Arguments>
{
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    public CameraPresetDefineCommand(IDirectionScopeResolver scopes, IDialogueNodeAccessor nodes)
    { this.scopes = scopes; this.nodes = nodes; }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var center = arguments.Center.WasProvided ? scopes.ResolvePosition(node, arguments.Center.Value!) : new DirectionPoint(50, 50);
        scopes.DefineCameraPreset(node, arguments.Name, new ViewPosePatch(ViewId.Main,
            DirectionValue.From(center), DirectionValue.From((float)arguments.Zoom.GetValueOrDefault(1)),
            DirectionValue.From((float)arguments.Rotation.GetValueOrDefault(0)), DirectionTransitionSpec.Cut));
        return ValueTask.CompletedTask;
    }
    public sealed record Arguments(
        [Primary] string Name,
        [Option] CommandValue<PositionRef> Center,
        [Option(Minimum = 0.001)] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation);
}

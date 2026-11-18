#nullable enable

using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Dialogue.Commands;

[Command("camera preset define", Category = "Direction")]
public sealed partial class CameraPresetDefineCommand : RuntimeCommand<CameraPresetDefineCommand.Arguments>
{
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly Majo.Runtime.Direction.Camera.CameraEnvironmentPresetCatalog environment;
    public CameraPresetDefineCommand(IDirectionScopeResolver scopes, IDialogueNodeAccessor nodes,
        Majo.Runtime.Direction.Camera.CameraEnvironmentPresetCatalog? environment = null)
    { this.scopes = scopes; this.nodes = nodes; this.environment = environment ?? new(); }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var options = new Dictionary<string, string>();
        if (arguments.Center.WasProvided) options["center"] = PositionRefConverter.Instance.Format(arguments.Center.Value!);
        if (arguments.Zoom.WasProvided) options["zoom"] = arguments.Zoom.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.Rotation.WasProvided) options["rotation"] = arguments.Rotation.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.Dolly.WasProvided) options["dolly"] = arguments.Dolly.Value!;
        if (arguments.Extends.WasProvided) options["extends"] = arguments.Extends.Value!;
        if (arguments.BackgroundOffset.WasProvided) options["backgroundOffset"] = arguments.BackgroundOffset.Value!;
        if (arguments.BackgroundScale.WasProvided) options["backgroundScale"] = arguments.BackgroundScale.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.Parallax.WasProvided) options["parallax"] = arguments.Parallax.Value!;
        if (arguments.ParallaxStrength.WasProvided) options["parallaxStrength"] = arguments.ParallaxStrength.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.ParallaxAnchor.WasProvided) options["parallaxAnchor"] = arguments.ParallaxAnchor.Value!;
        if (arguments.ParallaxLimit.WasProvided) options["parallaxLimit"] = arguments.ParallaxLimit.Value!;
        scopes.DefineCameraPreset(node, arguments.Name,
            CameraPresetDefinition.Resolve(node, CameraPresetDefinition.Parse(arguments.Name, options), scopes, environment));
        return ValueTask.CompletedTask;
    }
    public sealed record Arguments(
        [Primary] string Name,
        [Option] CommandValue<PositionRef> Center,
        [Option(Minimum = 0.001)] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Option(ValueHint = "neutral|near|back|number")] CommandValue<string> Dolly,
        [Option] CommandValue<string> Extends,
        [Option] CommandValue<string> BackgroundOffset,
        [Option(Minimum = 0.001)] CommandValue<double> BackgroundScale,
        [Option] CommandValue<string> Parallax,
        [Option(Minimum = 0)] CommandValue<double> ParallaxStrength,
        [Option] CommandValue<string> ParallaxAnchor,
        [Option] CommandValue<string> ParallaxLimit);
}

#if TOOLS
using Godot;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime;

[Tool]
public partial class MajoRuntimePlugin : EditorPlugin
{
    public override void _EnterTree()
    {
        var status = YarnDependencyValidator.ValidateProject(
            ProjectSettings.GlobalizePath("res://"));

        if (!status.IsCompatible)
        {
            GD.PushError($"Majo Runtime dependency check failed: {status.Message}");
        }
    }
}
#endif

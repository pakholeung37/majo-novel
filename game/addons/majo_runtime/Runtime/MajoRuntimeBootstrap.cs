using Godot;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime;

/// <summary>
/// Scene-tree composition root for Majo Runtime. M1 will attach the service
/// host to this node; M0 keeps only lifecycle and dependency validation.
/// </summary>
public partial class MajoRuntimeBootstrap : Node
{
    [Signal]
    public delegate void RuntimeInitializedEventHandler();

    [Signal]
    public delegate void RuntimeInitializationFailedEventHandler(string message);

    [Signal]
    public delegate void RuntimeShutdownEventHandler();

    public bool IsInitialized { get; private set; }

    public YarnDependencyStatus DependencyStatus { get; private set; } =
        YarnDependencyStatus.NotChecked;

    public override void _Ready()
    {
        DependencyStatus = YarnDependencyValidator.ValidateProject(
            ProjectSettings.GlobalizePath("res://"));

        if (!DependencyStatus.IsCompatible)
        {
            GD.PushError($"Majo Runtime dependency check failed: {DependencyStatus.Message}");
            EmitSignal(SignalName.RuntimeInitializationFailed, DependencyStatus.Message);
            return;
        }

        IsInitialized = true;
        EmitSignal(SignalName.RuntimeInitialized);
    }

    public override void _ExitTree()
    {
        if (!IsInitialized)
        {
            return;
        }

        IsInitialized = false;
        EmitSignal(SignalName.RuntimeShutdown);
    }
}

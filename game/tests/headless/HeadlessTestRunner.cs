using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner : Node
{
    private const double SuiteTimeoutSeconds = 10;
    private const string ForcedFailureArgument = "--majo-force-failure";

    public override void _Ready()
    {
        RunAndExitAsync();
    }

    private async void RunAndExitAsync()
    {
        try
        {
            await RunWithTimeoutAsync(RunSuiteAsync(), SuiteTimeoutSeconds);
            GD.Print("Majo Runtime headless tests passed.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"Majo Runtime headless tests failed: {exception}");
            GetTree().Quit(1);
        }
    }

    private async Task RunSuiteAsync()
    {
        if (OS.GetCmdlineUserArgs().Contains(ForcedFailureArgument, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Intentional headless failure probe.");
        }

        var dependencyStatus = YarnDependencyValidator.ValidateProject(
            ProjectSettings.GlobalizePath("res://"));
        Assert(dependencyStatus.IsCompatible, dependencyStatus.Message);

        var bootstrap = GetNode<MajoRuntimeBootstrap>("MajoRuntimeBootstrap");
        Assert(bootstrap.IsInitialized, "Bootstrap did not initialize after entering the scene tree.");
        Assert(bootstrap.DependencyStatus.IsCompatible, bootstrap.DependencyStatus.Message);

        var exited = false;
        bootstrap.TreeExited += () => exited = true;
        bootstrap.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(exited, "Bootstrap did not exit the scene tree during shutdown.");
        Assert(!IsInstanceValid(bootstrap), "Bootstrap node leaked after shutdown.");
    }

    private async Task RunWithTimeoutAsync(Task suite, double timeoutSeconds)
    {
        var startedAtMilliseconds = Time.GetTicksMsec();
        var timeoutMilliseconds = timeoutSeconds * 1_000;

        while (!suite.IsCompleted)
        {
            var elapsedMilliseconds = Time.GetTicksMsec() - startedAtMilliseconds;
            if (elapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException(
                    $"Headless test suite exceeded {timeoutSeconds:0.###} seconds.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await suite;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

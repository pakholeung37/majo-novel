using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner : Node
{
    private const double SuiteTimeoutSeconds = 25;
    private const string ForcedFailureArgument = "--majo-force-failure";
    private bool consumeNextLeftClick;
    private int consumedLeftClickCount;

    public override void _Ready()
    {
        RunAndExitAsync();
    }

    public override void _Input(InputEvent @event)
    {
        if (!consumeNextLeftClick ||
            @event is not InputEventMouseButton
            {
                ButtonIndex: MouseButton.Left,
                Pressed: true,
            })
        {
            return;
        }

        // Godot GUI Controls use the same handled flag before input reaches
        // _UnhandledInput. Model that boundary without depending on a display
        // server's pointer hit-testing in the headless suite.
        consumeNextLeftClick = false;
        consumedLeftClickCount++;
        GetViewport().SetInputAsHandled();
    }

    private async void RunAndExitAsync()
    {
        try
        {
            await RunWithTimeoutAsync(RunSuiteAsync(), SuiteTimeoutSeconds);
            await FinalizeEngineObjectsAsync();
            GD.Print("Majo Runtime headless tests passed.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"Majo Runtime headless tests failed: {exception}");
            await FinalizeEngineObjectsAsync();
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

        ValidateConfigurationDiagnostics();
        await ValidateAudioRuntimeAsync();
        await ValidateInputRuntimeAsync();
        await ValidateYarnVmStateBridgeAsync();
        await ValidateYarnHotReloadAsync();
        await ValidateScriptAndStateRuntimeAsync();
        await ValidateAutoAndHistoryRuntimeAsync();
        await ValidateSettingsRuntimeAsync();
        await ValidateLocaleRuntimeAsync();
        await ValidateDefaultPresentationAsync();
        await ValidateSuccessfulRuntimeLifecycleAsync();
        await ValidateLifecycleTimeoutAsync();
        await ValidateOperationKernelAsync();
        await ValidateDirectionRenderingAsync();
        await ValidateActorSystemAsync();
        await ValidateLayeredCharacterAsync();
        await ValidateYarnCommandAdapterAsync();
        await ValidatePlayableDemoAsync();
        await ValidateFailedConfigurationCleanupAsync();
        await ValidateDiagnosticsRuntimeAsync();
    }

    private static ActorDefinitionConfiguration CreateActorDefinition(
        string id,
        string factoryId,
        CharacterPositionPreset initialPosition,
        params (string Id, string Path)[] appearances)
    {
        var definition = new ActorDefinitionConfiguration
        {
            Id = id,
            FactoryId = factoryId,
            DefaultAppearance = appearances[0].Id,
            InitialPosition = initialPosition,
        };
        foreach (var appearance in appearances)
        {
            definition.Appearances.Add(new ActorAppearanceConfiguration
            {
                Id = appearance.Id,
                TexturePath = appearance.Path,
            });
        }

        return definition;
    }

    private async Task FinalizeEngineObjectsAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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

    private async Task WaitForConditionAsync(Func<bool> condition, double timeoutSeconds)
    {
        var startedAtMilliseconds = Time.GetTicksMsec();
        var timeoutMilliseconds = timeoutSeconds * 1_000;
        while (!condition())
        {
            if (Time.GetTicksMsec() - startedAtMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException(
                    $"Condition was not reached within {timeoutSeconds:0.###} seconds.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected exception '{typeof(TException).Name}' was not thrown.");
    }
}

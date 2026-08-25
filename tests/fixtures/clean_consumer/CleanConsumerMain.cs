using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.YarnBridge;

namespace CleanConsumer;

public partial class CleanConsumerMain : Node
{
    private const double SuiteTimeoutSeconds = 15;
    private const string ForcedFailureArgument = "--majo-force-failure";
    private const string ExpectedDependencyFailureArgument =
        "--majo-expect-dependency-failure";
    private static string? receivedCommandValue;

    public override void _Ready()
    {
        RunAndExitAsync();
    }

    private async void RunAndExitAsync()
    {
        try
        {
            if (OS.GetCmdlineUserArgs().Contains(ForcedFailureArgument, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Intentional clean-consumer failure probe.");
            }

            if (OS.GetCmdlineUserArgs().Contains(
                    ExpectedDependencyFailureArgument,
                    StringComparer.Ordinal))
            {
                await RunExpectedDependencyFailureAsync();
                await FinalizeEngineObjectsAsync();
                GD.Print("Clean consumer dependency failure probe passed.");
                GetTree().Quit(0);
                return;
            }

            await RunWithTimeoutAsync(RunVerificationAsync(), SuiteTimeoutSeconds);
            await FinalizeEngineObjectsAsync();
            GD.Print("Clean consumer fixture verification passed.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"Clean consumer fixture verification failed: {exception}");
            await FinalizeEngineObjectsAsync();
            GetTree().Quit(1);
        }
    }

    private async Task RunExpectedDependencyFailureAsync()
    {
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "DependencyFailureProbe",
            Configuration = MajoProjectConfiguration.CreateDefault(),
        };
        AddChild(bootstrap);

        var exception = await AssertThrowsAsync<RuntimeLifecycleException>(
            () => bootstrap.InitializationCompletion);
        Assert(
            exception.Message.Contains("Yarn dependency check failed", StringComparison.Ordinal),
            $"Unexpected dependency failure message: {exception.Message}");
        Assert(!bootstrap.IsInitialized, "Runtime initialized despite incompatible dependency metadata.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task RunVerificationAsync()
    {
        // 1. Dependency validation and layout check
        var projectRoot = ProjectSettings.GlobalizePath("res://");
        var dependencyStatus = YarnDependencyValidator.ValidateProject(projectRoot);
        Assert(dependencyStatus.IsCompatible, $"Yarn dependency validation failed: {dependencyStatus.Message}");

        YarnVmStateBridge.ValidateRuntimeLayout();

        // 2. Negative validation check
        var negativeStatus = YarnDependencyValidator.ValidateContents(
            "version=\"999.0.0\"",
            "<PackageReference Include=\"YarnSpinner\" Version=\"3.2.0\" />");
        Assert(!negativeStatus.IsCompatible, "YarnDependencyValidator should reject incompatible plugin version.");

        // 3. Configure and start Majo Runtime
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Presentation!.LettersPerSecond = 10_000;
        configuration.Presentation.UseFadeEffect = false;

        var lineProbe = new CleanLineProbe();
        receivedCommandValue = null;

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "CleanConsumerRuntime",
            Configuration = configuration,
        };

        bootstrap.ConfigureDialoguePresenters += presenters => presenters.Register(lineProbe);
        bootstrap.ConfigureCommands += commands =>
        {
            commands.Register<CleanCommandArguments>((arguments, _) =>
            {
                receivedCommandValue = arguments.Value;
                return Task.CompletedTask;
            });
        };

        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var services = bootstrap.Host!.Services;
        var dialogue = services.Get<IDialoguePlayer>();
        var presentation = services.Get<IDialoguePresentation>();
        var state = services.Get<IStateManager>();
        var variables = services.Get<IDialogueVariables>();
        var script = services.Get<IScriptPlayback>();

        // 4. Start dialogue and verify command execution
        await dialogue.StartAsync(new DialogueStartRequest("main", "Start"));
        await WaitForConditionAsync(() => presentation.IsLineActive, 2);
        dialogue.RequestHurryUp();
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);

        Assert(receivedCommandValue == "arg_from_yarn",
            $"Custom Yarn command was not executed or received wrong argument: '{receivedCommandValue}'");
        Assert(dialogue.CurrentLineId == "line:clean_line_1",
            $"Expected line 'line:clean_line_1' but was '{dialogue.CurrentLineId}'");
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance && script.CanCaptureCheckpoint,
            "ScriptPlayback did not reach safe checkpoint boundary.");

        // 5. Capture Game State Snapshot
        var snapshot = await state.CaptureGameAsync();
        Assert(snapshot.Records.ContainsKey(BuiltInStateKeys.DialogueVariables),
            "Snapshot missing DialogueVariables record.");
        Assert(snapshot.Records.ContainsKey(BuiltInStateKeys.ScriptPlayback),
            "Snapshot missing ScriptPlayback record.");

        // 6. Mutate variables and advance to next line
        variables.SetString("clean_var", "mutated_state");
        dialogue.RequestNextLine();
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:clean_line_2" && presentation.IsLineActive,
            2);
        dialogue.RequestHurryUp();
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);

        Assert(variables.Get("clean_var").StringValue == "advanced",
            "Variable should have been modified to 'advanced' by yarn script.");

        // 7. Restore Snapshot and verify round-trip
        await state.RestoreGameAsync(snapshot);
        await WaitForConditionAsync(
            () => lineProbe.Lines.Count >= 3 && presentation.IsLineActive,
            2,
            () => $"Restored line did not become active: " +
                $"probeCount={lineProbe.Lines.Count}, " +
                $"isLineActive={presentation.IsLineActive}, " +
                $"isDisplayComplete={presentation.IsLineDisplayComplete}, " +
                $"currentLine='{dialogue.CurrentLineId ?? "<none>"}', " +
                $"phase={script.Phase}.");
        dialogue.RequestHurryUp();
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);

        Assert(dialogue.CurrentLineId == "line:clean_line_1",
            $"Dialogue should have restored to 'line:clean_line_1' but was '{dialogue.CurrentLineId}'");
        Assert(variables.Get("clean_var").StringValue == "initial",
            $"Variable should have restored to 'initial' but was '{variables.Get("clean_var").StringValue}'");

        // 8. Advance through to completion
        dialogue.RequestNextLine();
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:clean_line_2" && lineProbe.Lines.Count >= 4,
            2);
        dialogue.RequestHurryUp();
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        dialogue.RequestNextLine();
        await dialogue.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        // 9. Shutdown and leak check
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap), "Bootstrap node leaked after shutdown.");
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
                    $"Clean consumer test exceeded {timeoutSeconds:0.###} seconds.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await suite;
    }

    private async Task WaitForConditionAsync(
        Func<bool> condition,
        double timeoutSeconds,
        Func<string>? timeoutMessage = null)
    {
        var startedAtMilliseconds = Time.GetTicksMsec();
        var timeoutMilliseconds = timeoutSeconds * 1_000;
        while (!condition())
        {
            if (Time.GetTicksMsec() - startedAtMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException(timeoutMessage?.Invoke() ??
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
            $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    private sealed class CleanLineProbe : IDialoguePresenter
    {
        public List<DialogueLine> Lines { get; } = new();

        public Task PresentLineAsync(
            DialogueLine line,
            DialoguePresentationContext context)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }

    [RuntimeCommand("clean_cmd", Category = "Clean")]
    public sealed class CleanCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public string Value { get; set; } = string.Empty;
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
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

        ValidateConfigurationDiagnostics();
        await ValidateSuccessfulRuntimeLifecycleAsync();
        await ValidateFailedConfigurationCleanupAsync();
    }

    private async Task ValidateSuccessfulRuntimeLifecycleAsync()
    {
        var service = new HeadlessLifecycleService();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "SuccessfulRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
            ConfigureServices = services =>
                services.AddSingleton<HeadlessLifecycleService>(_ => service),
        };

        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        Assert(bootstrap.IsInitialized, "Bootstrap did not initialize after entering the scene tree.");
        Assert(bootstrap.DependencyStatus.IsCompatible, bootstrap.DependencyStatus.Message);
        Assert(bootstrap.Host is not null, "Bootstrap did not expose its Runtime Host.");
        Assert(
            ReferenceEquals(service, bootstrap.Host.Services.Get<HeadlessLifecycleService>()),
            "Runtime Host resolved a different service instance.");
        Assert(service.InitializeCount == 1, "Runtime service was not initialized exactly once.");

        var firstSession = bootstrap.Host.SessionCancellationToken;
        await bootstrap.ResetRuntimeAsync();
        Assert(firstSession.IsCancellationRequested, "Reset did not cancel the previous session.");
        Assert(service.ResetCount == 1, "Runtime service was not reset exactly once.");

        var exited = false;
        bootstrap.TreeExited += () => exited = true;
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(exited, "Bootstrap did not exit the scene tree during shutdown.");
        Assert(!IsInstanceValid(bootstrap), "Bootstrap node leaked after shutdown.");
        Assert(service.ShutdownCount == 1, "Runtime service was not shut down exactly once.");

        await bootstrap.ShutdownCompletion;
        Assert(service.ShutdownCount == 1, "Repeated shutdown completion changed service state.");
    }

    private async Task ValidateFailedConfigurationCleanupAsync()
    {
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "InvalidRuntime",
            Configuration = new MajoProjectConfiguration(),
        };
        var failureMessage = string.Empty;
        var exited = false;
        bootstrap.RuntimeInitializationFailed += message => failureMessage = message;
        bootstrap.TreeExited += () => exited = true;

        AddChild(bootstrap);
        try
        {
            await bootstrap.InitializationCompletion;
            throw new InvalidOperationException("Invalid configuration unexpectedly initialized.");
        }
        catch (MajoConfigurationException)
        {
        }

        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(failureMessage.Contains("Runtime", StringComparison.Ordinal),
            "Configuration failure did not include a diagnostic path.");
        Assert(bootstrap.Host is null, "Invalid configuration created a Runtime Host.");
        Assert(exited, "Invalid bootstrap did not exit the scene tree.");
        Assert(!IsInstanceValid(bootstrap), "Invalid bootstrap node leaked after failure.");
    }

    private static void ValidateConfigurationDiagnostics()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Actors!.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "hero",
            AssetPath = "res://missing/hero-a.png",
        });
        configuration.Actors.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "hero",
            AssetPath = "res://missing/hero-b.png",
        });
        configuration.Dialogue!.RequiredYarnRuntimeVersion = "99.0.0";

        var validation = configuration.Validate(_ => false);

        Assert(!validation.IsValid, "Invalid actor configuration passed validation.");
        Assert(validation.Issues.Any(issue =>
                issue.Message.Contains("duplicated", StringComparison.Ordinal)),
            "Duplicate actor ID was not diagnosed.");
        Assert(validation.Issues.Count(issue =>
                issue.Message.Contains("does not exist", StringComparison.Ordinal)) == 2,
            "Invalid actor resource paths were not diagnosed.");
        Assert(validation.Issues.Any(issue =>
                issue.Path.Contains("RequiredYarnRuntimeVersion", StringComparison.Ordinal)),
            "Invalid Yarn runtime version was not diagnosed.");
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

    private sealed class HeadlessLifecycleService : IRuntimeService
    {
        public int InitializeCount { get; private set; }

        public int ResetCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            ShutdownCount++;
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateSuccessfulRuntimeLifecycleAsync()
    {
        var service = new HeadlessLifecycleService();
        var shutdownSignalCount = 0;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "SuccessfulRuntime",
            Configuration = MajoProjectConfiguration.CreateDefault(),
            ConfigureServices = services =>
                services.AddSingleton<HeadlessLifecycleService>(_ => service),
        };
        bootstrap.RuntimeShutdown += () => shutdownSignalCount++;

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
        Assert(shutdownSignalCount == 1,
            "Runtime shutdown signal was not emitted exactly once during tree exit.");

        await bootstrap.ShutdownCompletion;
        Assert(service.ShutdownCount == 1, "Repeated shutdown completion changed service state.");
        Assert(shutdownSignalCount == 1,
            "Repeated shutdown completion emitted another Runtime shutdown signal.");
    }

    private async Task ValidateLifecycleTimeoutAsync()
    {
        var service = new NonCooperativeInitializationService();
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Runtime!.LifecycleTimeoutSeconds = 0.05;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "TimedOutRuntime",
            Configuration = configuration,
            ConfigureServices = services =>
                services.AddSingleton<NonCooperativeInitializationService>(_ => service),
        };
        var exited = false;
        bootstrap.TreeExited += () => exited = true;

        AddChild(bootstrap);
        Exception initializationFailure = null;
        try
        {
            await bootstrap.InitializationCompletion;
        }
        catch (Exception exception)
        {
            initializationFailure = exception;
        }
        finally
        {
            service.CompleteInitialization();
        }

        Assert(initializationFailure is TimeoutException,
            "A service that ignored cancellation did not trigger the hard lifecycle timeout.");
        Assert(initializationFailure.Message.Contains("initialization", StringComparison.Ordinal),
            "Lifecycle timeout did not identify the timed-out operation.");

        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(service.ShutdownCount == 1,
            "A late-completing initialization was not subsequently shut down.");
        Assert(exited && !IsInstanceValid(bootstrap),
            "Timed-out Runtime bootstrap leaked after late initialization cleanup.");
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
            DefaultAppearance = "default",
            Appearances =
            {
                new ActorAppearanceConfiguration
                {
                    Id = "default",
                    TexturePath = "res://missing/hero-a.png",
                },
            },
        });
        configuration.Actors.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "hero",
            DefaultAppearance = "default",
            Appearances =
            {
                new ActorAppearanceConfiguration
                {
                    Id = "default",
                    TexturePath = "res://missing/hero-b.png",
                },
            },
        });
        configuration.Dialogue!.RequiredYarnRuntimeVersion = "99.0.0";
        configuration.Presentation!.FontSize = 0;
        configuration.Presentation.LettersPerSecond = 0;
        configuration.Presentation.FadeUpDurationSeconds = double.NaN;
        configuration.Audio!.MasterVolume = 2;
        configuration.Audio.DefaultFadeDurationSeconds = double.NaN;
        configuration.Audio.VoiceLocale = string.Empty;
		configuration.Audio.BgmPath = "..";
		configuration.Resources!.FailOnMissingAssets = true;
        configuration.Auto!.ExtraDelaySeconds = double.NaN;
        configuration.History!.Capacity = 0;

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
        Assert(validation.Issues.Count(issue =>
                issue.Path.StartsWith("Presentation", StringComparison.Ordinal)) == 3,
            "Invalid M8 Presentation values were not diagnosed together.");
        Assert(validation.Issues.Count(issue =>
                issue.Path.StartsWith("Audio", StringComparison.Ordinal)) == 4,
            "Invalid M9 Audio values were not diagnosed together.");
        Assert(validation.Issues.Count(issue =>
                issue.Path.StartsWith("Auto", StringComparison.Ordinal)) == 1 &&
                validation.Issues.Count(issue =>
                    issue.Path.StartsWith("History", StringComparison.Ordinal)) == 1,
            "Invalid M10 Auto/History values were not diagnosed together.");

        var customActorConfiguration = MajoProjectConfiguration.CreateDefault();
        customActorConfiguration.Actors!.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "custom",
            FactoryId = "custom-payload",
            FactoryConfiguration = new Resource(),
        });
        Assert(customActorConfiguration.Validate(_ => false).IsValid,
            "Factory-owned Actor configuration was incorrectly validated as a texture Actor.");
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

    private sealed class NonCooperativeInitializationService : IRuntimeService
    {
        private readonly TaskCompletionSource initialization =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ShutdownCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => initialization.Task;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public void CompleteInitialization() => initialization.TrySetResult();
    }
}

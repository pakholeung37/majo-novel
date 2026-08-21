#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    [Signal]
    public delegate void RuntimeInitializedEventHandler();

    [Signal]
    public delegate void RuntimeInitializationFailedEventHandler(string message);

    [Signal]
    public delegate void RuntimeShutdownEventHandler();

    private readonly TaskCompletionSource initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource bootstrapLifetime = new();
    private Task? shutdownTask;
    private bool isExitingTree;
    private bool isBootstrapLifetimeDisposed;

    [Export]
    public MajoProjectConfiguration? Configuration { get; set; }

    public Action<RuntimeServiceRegistry>? ConfigureServices { get; set; }

    public RuntimeHost? Host { get; private set; }

    public bool IsInitialized => Host?.State == RuntimeHostState.Initialized;

    public Task InitializationCompletion => initializationCompletion.Task;

    public Task ShutdownCompletion => shutdownCompletion.Task;

    public YarnDependencyStatus DependencyStatus { get; private set; } =
        YarnDependencyStatus.NotChecked;

    public override void _Ready()
    {
        _ = InitializeRuntimeAsync();
    }

    public override void _ExitTree()
    {
        isExitingTree = true;
        CancelBootstrapLifetime();
        shutdownTask ??= ShutdownHostAsync();
    }

    public async Task ResetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        if (Host is null)
        {
            throw new RuntimeLifecycleException("Majo Runtime has not created a host.");
        }

        await ExecuteWithLifecycleTimeoutAsync(
            token => Host.ResetAsync(token),
            cancellationToken,
            "reset");
    }

    public async Task RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        CancelBootstrapLifetime();
        shutdownTask ??= ShutdownHostAsync(cancellationToken);
        await shutdownTask;
        await shutdownCompletion.Task;

        if (IsInsideTree())
        {
            QueueFree();
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        try
        {
            DependencyStatus = YarnDependencyValidator.ValidateProject(
                ProjectSettings.GlobalizePath("res://"));

            if (!DependencyStatus.IsCompatible)
            {
                throw new RuntimeLifecycleException(
                    $"Yarn dependency check failed: {DependencyStatus.Message}");
            }

            if (Configuration is null)
            {
                throw new MajoConfigurationException(new[]
                {
                    new ConfigurationIssue(nameof(Configuration), "Configuration is required."),
                });
            }

            var validation = Configuration.Validate();
            validation.ThrowIfInvalid();

            var services = new RuntimeServiceRegistry();
            RegisterDefaultServices(services);
            ConfigureServices?.Invoke(services);
            Host = services.Build(new GodotRuntimeLogger());
            await ExecuteWithLifecycleTimeoutAsync(
                token => Host.InitializeAsync(token),
                bootstrapLifetime.Token,
                "initialization");

            if (isExitingTree)
            {
                throw new OperationCanceledException(
                    "Majo Runtime node exited during initialization.",
                    bootstrapLifetime.Token);
            }

            initializationCompletion.TrySetResult();
            EmitSignal(SignalName.RuntimeInitialized);
        }
        catch (Exception exception)
        {
            initializationCompletion.TrySetException(exception);
            _ = initializationCompletion.Task.Exception;
            if (!isExitingTree)
            {
                GD.PushError($"Majo Runtime initialization failed: {exception}");
                EmitSignal(SignalName.RuntimeInitializationFailed, exception.Message);
                QueueFree();
            }
        }
    }

    private void RegisterDefaultServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<IFrameClock>(_ => new GodotFrameClock(GetTree()))
            .AddSingleton<IOperationScheduler>(
                resolver => new OperationScheduler(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>(),
                    new GodotRuntimeLogger()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime));
    }

    private async Task ShutdownHostAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Host is not null)
            {
                await ExecuteWithLifecycleTimeoutAsync(
                    token => Host.ShutdownAsync(token),
                    cancellationToken,
                    "shutdown");
            }

            shutdownCompletion.TrySetResult();
            if (!isExitingTree && IsInsideTree())
            {
                EmitSignal(SignalName.RuntimeShutdown);
            }
        }
        catch (Exception exception)
        {
            shutdownCompletion.TrySetException(exception);
            _ = shutdownCompletion.Task.Exception;
            GD.PushError($"Majo Runtime shutdown failed: {exception}");
        }
        finally
        {
            if (!isBootstrapLifetimeDisposed)
            {
                bootstrapLifetime.Dispose();
                isBootstrapLifetimeDisposed = true;
            }
        }
    }

    private void CancelBootstrapLifetime()
    {
        if (!isBootstrapLifetimeDisposed && !bootstrapLifetime.IsCancellationRequested)
        {
            bootstrapLifetime.Cancel();
        }
    }

    private async Task ExecuteWithLifecycleTimeoutAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string operationName)
    {
        var timeoutSeconds = Configuration?.Runtime?.LifecycleTimeoutSeconds ?? 10;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await operation(linked.Token);
        }
        catch (Exception exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Majo Runtime {operationName} exceeded {timeoutSeconds:0.###} seconds.",
                exception);
        }
    }
}

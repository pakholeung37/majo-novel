#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private async Task ShutdownHostAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            yarnCommandAdapter.CommandFailed -= HandleCommandFailed;
            yarnCommandAdapter.Detach();
            yarnDialogueBackend = null;
            dialoguePlayer = null;
            dialoguePresentation = null;

            if (Host is not null)
            {
                // If initialization exceeded its deadline, RuntimeHost may still
                // own a service whose initializer deliberately ignored
                // cancellation. Let that initialization finish before asking the
                // host to shut down; applying the short shutdown deadline here
                // would cancel while the host is waiting on its lifecycle gate and
                // leave the late-completing service uncleaned.
                if (Host.State == RuntimeHostState.Initializing)
                {
                    await Host.ShutdownAsync(CancellationToken.None);
                }
                else
                {
                    await ExecuteWithLifecycleTimeoutAsync(
                        token => Host.ShutdownAsync(token),
                        "shutdown",
                        cancellationToken);
                }
            }

            TeardownDiagnosticsAndOverlays();

            shutdownCompletion.TrySetResult();
            if (!isExitingTree && IsInsideTree())
            {
                EmitRuntimeShutdownOnce();
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

    private void HandleCommandFailed(RuntimeCommandException failure) => EmitSignal(
        SignalName.RuntimeCommandFailed,
        failure.CommandName,
        failure.Message);

    private void EmitRuntimeShutdownOnce()
    {
        if (isRuntimeShutdownSignalEmitted || Host is null)
        {
            return;
        }

        isRuntimeShutdownSignalEmitted = true;
        EmitSignal(SignalName.RuntimeShutdown);
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
        string operationName,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Configuration?.Runtime?.LifecycleTimeoutSeconds ?? 10;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task operationTask;

        try
        {
            operationTask = operation(operationCancellation.Token);
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        using var deadlineCancellation = new CancellationTokenSource();
        var cancellationSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationSignal.TrySetResult());
        var deadlineTask = Task.Delay(timeout, deadlineCancellation.Token);
        var completedTask = await Task.WhenAny(
            operationTask,
            deadlineTask,
            cancellationSignal.Task);

        if (completedTask == operationTask)
        {
            deadlineCancellation.Cancel();
            try
            {
                await operationTask;
            }
            finally
            {
                operationCancellation.Dispose();
            }

            return;
        }

        var cancellationException = TryCancel(operationCancellation);
        deadlineCancellation.Cancel();
        _ = ObserveTimedOutOperationAsync(operationTask, operationCancellation);

        cancellationToken.ThrowIfCancellationRequested();

        var message =
            $"Majo Runtime {operationName} exceeded {timeoutSeconds:0.###} seconds.";
        throw cancellationException is null
            ? new TimeoutException(message)
            : new TimeoutException(message, cancellationException);
    }

    private static Exception? TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task ObserveTimedOutOperationAsync(
        Task operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

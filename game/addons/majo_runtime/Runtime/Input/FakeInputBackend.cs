#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Input;

public sealed class FakeInputBackend : IInputBackend
{
    private bool isInitialized;

    public event Action<InputActivation>? Activated;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = false;
        Activated = null;
        return Task.CompletedTask;
    }

    public void Activate(InputActionId action, ulong frame, InputDeviceKind device =
        InputDeviceKind.Unknown)
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException(
                "Fake Input Backend must be initialized before emitting input.");
        }

        Activated?.Invoke(new InputActivation(action, frame, device));
    }
}

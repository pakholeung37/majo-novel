#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class DelegatingYarnCommandAdapter : IYarnCommandAdapter
{
    private IYarnCommandAdapter? target;

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure => target?.LastFailure;

    public bool AbortOnRecoverableFailure => target?.AbortOnRecoverableFailure ?? true;

    public bool HasActiveCommands => target?.HasActiveCommands ?? false;

    public IReadOnlyList<RuntimeCommandActivity> ActiveCommands =>
        target?.ActiveCommands ?? Array.Empty<RuntimeCommandActivity>();

    public void Attach(IYarnCommandAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (target is not null)
        {
            target.CommandFailed -= HandleCommandFailed;
        }

        target = adapter;
        target.CommandFailed += HandleCommandFailed;
    }

    public void Detach()
    {
        if (target is not null)
        {
            target.CommandFailed -= HandleCommandFailed;
            target = null;
        }
    }

    public void CancelDialogue() => target?.CancelDialogue();

    public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) =>
        target?.WaitForQuiescenceAsync(cancellationToken) ?? Task.CompletedTask;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        target?.InitializeAsync(cancellationToken) ?? Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        target?.ResetAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (target is not null)
        {
            try
            {
                await target.ShutdownAsync(cancellationToken);
            }
            finally
            {
                Detach();
            }
        }
    }

    private void HandleCommandFailed(RuntimeCommandException failure)
    {
        CommandFailed?.Invoke(failure);
    }
}

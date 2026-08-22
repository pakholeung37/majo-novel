#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

internal sealed class AwaitScopeCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly Stack<AwaitScope> scopes = new();
    private CancellationToken dialogueToken;
    private CancellationTokenRegistration dialogueCancellation;
    private bool isDisposed;

    public int Depth
    {
        get
        {
            lock (gate)
            {
                return scopes.Count;
            }
        }
    }

    public void Begin(bool complete, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureNotDisposed();
            BindDialogue(cancellationToken);
            scopes.Push(new AwaitScope(complete));
        }
    }

    public void Track(IOperationHandle operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            EnsureNotDisposed();
            foreach (var scope in scopes)
            {
                scope.Operations.Add(operation);
            }
        }
    }

    public async Task EndAsync(CancellationToken cancellationToken)
    {
        AwaitScope scope;
        lock (gate)
        {
            EnsureNotDisposed();
            BindDialogue(cancellationToken);
            if (!scopes.TryPop(out scope!))
            {
                throw new RuntimeCommandException(
                    "endAwait", "No matching await block is open.");
            }
        }

        if (scope.Complete)
        {
            foreach (var operation in scope.Operations)
            {
                if (operation.State == OperationState.Running)
                {
                    operation.Complete();
                }
            }
        }

        await Task.WhenAll(scope.Operations.Select(
            operation => operation.Completion.WaitAsync(cancellationToken)));
    }

    public void Reset()
    {
        lock (gate)
        {
            ClearScopes();
            dialogueCancellation.Dispose();
            dialogueCancellation = default;
            dialogueToken = default;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            ClearScopes();
            dialogueCancellation.Dispose();
        }
    }

    private void BindDialogue(CancellationToken cancellationToken)
    {
        if (dialogueToken == cancellationToken)
        {
            return;
        }

        ClearScopes();
        dialogueCancellation.Dispose();
        dialogueToken = cancellationToken;
        dialogueCancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(CancelDialogue)
            : default;
    }

    private void CancelDialogue()
    {
        lock (gate)
        {
            ClearScopes();
        }
    }

    private void ClearScopes() => scopes.Clear();

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }

    private sealed class AwaitScope
    {
        public AwaitScope(bool complete)
        {
            Complete = complete;
        }

        public bool Complete { get; }

        public HashSet<IOperationHandle> Operations { get; } =
            new(ReferenceEqualityComparer.Instance);
    }
}

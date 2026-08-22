#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandDispatcher : IDisposable
{
    private readonly CommandBinder binder;
    private readonly IActorManager? actors;
    private readonly IOperationScheduler? operations;
    private readonly IServiceResolver? services;
    private readonly AwaitScopeCoordinator awaitScopes = new();

    public RuntimeCommandDispatcher(
        CommandBinder binder,
        IActorManager? actors = null,
        IOperationScheduler? operations = null,
        IServiceResolver? services = null)
    {
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.actors = actors;
        this.operations = operations;
        this.services = services;
    }

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure { get; private set; }

    public void Validate(RuntimeCommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var command in catalog.Commands)
        {
            binder.Validate(command.Descriptor);
        }
    }

    public async Task ExecuteAsync(
        RuntimeCommandRegistration registration,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(arguments);
        var context = new RuntimeCommandContext(
            actors,
            operations,
            services,
            cancellationToken,
            awaitScopes.Track,
            awaitScopes);
        try
        {
            await registration.BindAndExecuteAsync(binder, arguments, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // DialogueRunner owns dialogue cancellation; it is not a command failure.
        }
        catch (RuntimeCommandException exception)
        {
            ReportFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                registration.Name,
                exception.Message,
                exception);
            ReportFailure(failure);
            throw failure;
        }
    }

    public void Reset()
    {
        LastFailure = null;
        awaitScopes.Reset();
    }

    public void Dispose() => awaitScopes.Dispose();

    private void ReportFailure(RuntimeCommandException failure)
    {
        LastFailure = failure;
        CommandFailed?.Invoke(failure);
    }
}

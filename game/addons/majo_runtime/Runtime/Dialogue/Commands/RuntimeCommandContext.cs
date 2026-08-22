#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandContext
{
    internal RuntimeCommandContext(
        IActorManager? actors,
        IOperationScheduler? operations,
        IServiceResolver? services,
        CancellationToken cancellationToken)
    {
        Actors = actors;
        Operations = operations;
        Services = services;
        CancellationToken = cancellationToken;
    }

    public IActorManager? Actors { get; }

    public IOperationScheduler? Operations { get; }

    public IServiceResolver? Services { get; }

    public CancellationToken CancellationToken { get; }

    public TService Require<TService>(TService? service, string serviceName)
        where TService : class => service ?? throw new RuntimeCommandException(
            "extension", $"Runtime command context does not provide {serviceName}.");

    public IOperationHandle Track(IOperationHandle operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!CancellationToken.CanBeCanceled)
        {
            return operation;
        }

        var registration = CancellationToken.Register(operation.Cancel);
        _ = DisposeRegistrationWhenCompletedAsync(operation, registration);
        return operation;
    }

    private static async Task DisposeRegistrationWhenCompletedAsync(
        IOperationHandle operation,
        CancellationTokenRegistration registration)
    {
        try
        {
            await operation.Completion;
        }
        catch
        {
            // OperationScheduler owns fault reporting; this observer only releases registration.
        }
        finally
        {
            registration.Dispose();
        }
    }
}

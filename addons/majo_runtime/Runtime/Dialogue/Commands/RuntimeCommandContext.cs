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
        IOperationRunner? operations,
        IServiceResolver? services,
        CancellationToken cancellationToken,
        CommandBindingContext? binding = null)
    {
        Actors = actors;
        Operations = operations;
        Services = services;
        CancellationToken = cancellationToken;
        Binding = binding;
    }

    public IActorManager? Actors { get; }

    public IOperationRunner? Operations { get; }

    public IServiceResolver? Services { get; }

    public CancellationToken CancellationToken { get; }

    public CommandBindingContext? Binding { get; }

    public static TService Require<TService>(TService? service, string serviceName)
        where TService : class => service ?? throw new RuntimeCommandException(
            "extension", $"Runtime command context does not provide {serviceName}.");
}

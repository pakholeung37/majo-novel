#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Save;

public interface ISaveProductAccessor : IRuntimeService
{
    ISaveProductManager? SaveProduct { get; }
}

public sealed class DelegateSaveProductAccessor : ISaveProductAccessor
{
    private readonly Func<ISaveProductManager?> provider;

    public DelegateSaveProductAccessor(Func<ISaveProductManager?> provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public ISaveProductManager? SaveProduct => provider();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

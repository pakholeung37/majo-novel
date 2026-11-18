#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Script;

public interface IScriptCatalogBackend
{
    Task<ScriptDescriptor> ResolveAsync(
        ScriptId id,
        CancellationToken cancellationToken = default);
}

public interface IScriptManager : IRuntimeService
{
    Task<ScriptDescriptor> ResolveAsync(
        ScriptId id,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptManager : IScriptManager
{
    private readonly IScriptCatalogBackend backend;

    public ScriptManager(IScriptCatalogBackend backend)
    {
        this.backend = backend ?? throw new System.ArgumentNullException(nameof(backend));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        return Task.CompletedTask;
    }

    public Task<ScriptDescriptor> ResolveAsync(
        ScriptId id,
        CancellationToken cancellationToken = default) =>
        backend.ResolveAsync(id, cancellationToken);
}

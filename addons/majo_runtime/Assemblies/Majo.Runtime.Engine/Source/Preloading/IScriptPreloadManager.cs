#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Preloading;

public interface IScriptPreloadManager : IRuntimeService
{
    string? CurrentWindowId { get; }

    Task PrepareAsync(
        ScriptPreloadWindow window,
        ResourcePreloadContext? context = null,
        CancellationToken cancellationToken = default);

    void ReleaseCurrent();
}

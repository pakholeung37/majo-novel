#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Plugins;

namespace Majo.Runtime;

// Command factories and plugins can access the complete initialized graph here.
// Plugins deactivate while commands and every other runtime service are still alive.
internal sealed class BootstrapRuntimeBindings : IRuntimeService, IRuntimeActivation
{
    private readonly Func<IServiceResolver, CancellationToken, Task> attachCommands;
    private readonly Func<IServiceResolver, PluginContext> createContext;
    private IPluginManager? plugins;

    public BootstrapRuntimeBindings(
        Func<IServiceResolver, CancellationToken, Task> attachCommands,
        Func<IServiceResolver, PluginContext> createContext)
    {
        this.attachCommands = attachCommands;
        this.createContext = createContext;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ActivateAsync(IServiceResolver services, CancellationToken cancellationToken)
    {
        plugins = services.Get<IPluginManager>();
        await attachCommands(services, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await plugins.InitializeAllAsync(createContext(services), cancellationToken);
    }

    public Task DeactivateAsync(CancellationToken cancellationToken) =>
        plugins?.ShutdownAllAsync(cancellationToken) ?? Task.CompletedTask;
}

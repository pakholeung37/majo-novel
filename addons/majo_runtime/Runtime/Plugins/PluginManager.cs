#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Plugins;

public interface IPluginManager : IRuntimeService
{
    IReadOnlyList<IMajoPlugin> Plugins { get; }

    void Register(IMajoPlugin plugin);

    void ConfigureServices(RuntimeServiceRegistry services);

    void Configure(PluginRegistrationContext context);

    void SealRegistration();


    void InitializeAll(PluginContext context);

    Task InitializeAllAsync(PluginContext context, CancellationToken cancellationToken);

    Task ResetAllAsync(CancellationToken cancellationToken);

    Task ShutdownAllAsync(CancellationToken cancellationToken);
}

public sealed class PluginManager : IPluginManager
{
    private readonly object gate = new();
    private readonly List<IMajoPlugin> plugins = new();
    private PluginContext? context;
    private bool registrationSealed;

    public IReadOnlyList<IMajoPlugin> Plugins
    {
        get
        {
            lock (gate)
            {
                return plugins.ToArray();
            }
        }
    }

    public void Register(IMajoPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (gate)
        {
            if (registrationSealed)
                throw new PluginRegistrationSealedException();
            if (string.IsNullOrWhiteSpace(plugin.Id))
                throw new ArgumentException("Plugin ID cannot be empty.", nameof(plugin));
            if (plugins.Any(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicatePluginException(plugin.Id);
            }

            plugins.Add(plugin);
        }
    }

    public void ConfigureServices(RuntimeServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IMajoPlugin[] snapshot;
        lock (gate)
        {
            snapshot = plugins.ToArray();
        }

        foreach (var plugin in OrderPlugins(snapshot))
            plugin.ConfigureServices(services);
    }

    public void Configure(PluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IMajoPlugin[] snapshot;
        lock (gate) snapshot = plugins.ToArray();
        foreach (var plugin in OrderPlugins(snapshot)) plugin.Configure(context);
    }

    public void SealRegistration()
    {
        lock (gate) registrationSealed = true;
    }

    public void InitializeAll(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IMajoPlugin[] snapshot;
        lock (gate)
        {
            snapshot = plugins.ToArray();
        }

        var ordered = OrderPlugins(snapshot);
        var initialized = new List<IMajoPlugin>();
        foreach (var plugin in ordered)
        {
            try
            {
                initialized.Add(plugin);
                plugin.Initialize(context);
                context.Logger.Info("plugin", "plugin.loaded", $"Loaded plugin: {plugin.Name} (v{plugin.Version})");
            }
            catch (Exception ex)
            {
                for (var index = initialized.Count - 1; index >= 0; index--)
                {
                    try { initialized[index].ShutdownAsync(context, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch { }
                }
                context.Logger.Error("plugin", "plugin.failed", $"Failed to initialize plugin '{plugin.Id}': {ex.Message}", ex);
                throw;
            }
        }
        this.context = context;
    }

    public async Task InitializeAllAsync(PluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IMajoPlugin[] snapshot;
        lock (gate) snapshot = plugins.ToArray();
        var ordered = OrderPlugins(snapshot);
        var initialized = new List<IMajoPlugin>();
        try
        {
            foreach (var plugin in ordered)
            {
                initialized.Add(plugin);
                await plugin.InitializeAsync(context, cancellationToken);
                context.Logger.Info("plugin", "plugin.loaded", $"Loaded plugin: {plugin.Name} (v{plugin.Version})");
            }
            this.context = context;
        }
        catch
        {
            for (var index = initialized.Count - 1; index >= 0; index--)
            {
                try { await initialized[index].ShutdownAsync(context, CancellationToken.None); }
                catch { }
            }
            throw;
        }
    }

    public async Task ResetAllAsync(CancellationToken cancellationToken)
    {
        var current = context;
        if (current is null) return;
        IMajoPlugin[] snapshot;
        lock (gate) snapshot = plugins.ToArray();
        var ordered = OrderPlugins(snapshot);
        var failures = new List<Exception>();
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            try { await ordered[index].ResetAsync(current, cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0)
            throw new AggregateException("One or more plugins failed to reset.", failures);
    }

    public async Task ShutdownAllAsync(CancellationToken cancellationToken)
    {
        var current = context;
        if (current is null) return;
        IMajoPlugin[] snapshot;
        lock (gate) snapshot = plugins.ToArray();
        var ordered = OrderPlugins(snapshot);
        var failures = new List<Exception>();
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            try { await ordered[index].ShutdownAsync(current, cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        context = null;
        if (failures.Count > 0)
            throw new AggregateException("One or more plugins failed to shut down.", failures);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        return ResetAllAsync(cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        return ShutdownAllAsync(cancellationToken);
    }

    private static List<IMajoPlugin> OrderPlugins(IMajoPlugin[] snapshot)
    {
        var byId = snapshot.ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IMajoPlugin>(snapshot.Length);
        var states = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        foreach (var plugin in snapshot)
            Visit(plugin, byId, states, path, ordered);
        return ordered;
    }

    private static void Visit(
        IMajoPlugin plugin,
        IReadOnlyDictionary<string, IMajoPlugin> byId,
        IDictionary<string, VisitState> states,
        IList<string> path,
        ICollection<IMajoPlugin> ordered)
    {
        if (states.TryGetValue(plugin.Id, out var state))
        {
            if (state == VisitState.Visited) return;
            var cycleStart = path.IndexOf(plugin.Id);
            throw new PluginDependencyException(
                $"Plugin dependency cycle detected: {string.Join(" -> ", path.Skip(cycleStart).Append(plugin.Id))}");
        }
        states[plugin.Id] = VisitState.Visiting;
        path.Add(plugin.Id);
        foreach (var dependency in plugin.Dependencies ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(dependency) || !byId.TryGetValue(dependency, out var dependencyPlugin))
                throw new PluginDependencyException(
                    $"Plugin '{plugin.Id}' requires missing plugin '{dependency}'.");
            Visit(dependencyPlugin, byId, states, path, ordered);
        }
        path.RemoveAt(path.Count - 1);
        states[plugin.Id] = VisitState.Visited;
        ordered.Add(plugin);
    }

    private enum VisitState { Visiting, Visited }
}

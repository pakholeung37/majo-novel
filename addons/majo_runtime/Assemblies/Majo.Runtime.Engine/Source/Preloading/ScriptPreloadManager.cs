#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Preloading;

public sealed class ScriptPreloadManager : IScriptPreloadManager, IDisposable
{
    private readonly IResourceManager resources;
    private readonly IActorManager actors;
    private readonly IRuntimeLogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IResourceGroupLease? currentResources;
    private bool isInitialized;

    public ScriptPreloadManager(
        IResourceManager resources,
        IActorManager actors,
        IRuntimeLogger? logger = null)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public string? CurrentWindowId { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task PrepareAsync(
        ScriptPreloadWindow window,
        ResourcePreloadContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        EnsureInitialized();
        await gate.WaitAsync(cancellationToken);
        IResourceGroupLease? candidateResources = null;
        try
        {
            logger.Info(
                "preload",
                "preload.window_started",
                $"Script preload window '{window.Id}' started.");
            if (window.Resources is not null)
            {
                candidateResources = resources.PreloadAsync(
                    window.Resources,
                    context,
                    cancellationToken);
            }

            if (candidateResources is not null)
            {
                // Actor constructors are still allowed to use synchronous cache
                // reads. Finish the resource barrier first so they never collide
                // with an in-flight load of the same texture.
                await candidateResources.Completion;
            }

            await Task.WhenAll(window.Actors.Select(warmup =>
                WarmActorAsync(warmup, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();

            var previousResources = currentResources;
            currentResources = candidateResources;
            candidateResources = null;
            CurrentWindowId = window.Id;
            previousResources?.Release();
            logger.Info(
                "preload",
                "preload.window_ready",
                $"Script preload window '{window.Id}' is ready.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error(
                "preload",
                "preload.window_failed",
                $"Script preload window '{window.Id}' failed.",
                exception);
            throw;
        }
        finally
        {
            candidateResources?.Release();
            gate.Release();
        }
    }

    public void ReleaseCurrent()
    {
        currentResources?.Release();
        currentResources = null;
        CurrentWindowId = null;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ReleaseCurrent();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        await ResetAsync(cancellationToken);
        isInitialized = false;
    }

    private async Task WarmActorAsync(
        ActorWarmupRequest warmup,
        CancellationToken cancellationToken)
    {
        var actor = await actors.GetOrCreateAsync(
            new ActorSpawnRequest(
                warmup.ActorId,
                warmup.FallbackFactoryId,
                warmup.Appearances[0],
                warmup.InitialPosition,
                warmup.RequiredFactoryId,
                ActorCreationMode.Dormant),
            cancellationToken);
        foreach (var appearance in warmup.Appearances)
        {
            await actor.PrepareAppearanceAsync(appearance, cancellationToken);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException(
                "ScriptPreloadManager is not initialized.");
        }
    }

    public void Dispose() => gate.Dispose();
}

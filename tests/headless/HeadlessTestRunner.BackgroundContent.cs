#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateBackgroundContentAsync()
    {
        using var image = Image.CreateEmpty(100, 50, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(image);
        var loader = new BackgroundProbeLoader(texture);
        var root = new Node2D();
        var host = new Control { Size = new Vector2(200, 200) };
        AddChild(host);
        host.AddChild(root);
        var definition = new ActorDefinition("content-probe", ActorFactoryIds.Background,
            new TextureActorDefinitionData("content-probe", "backgrounds", "day", new Dictionary<string, string>()));
        var actor = new BackgroundActor(definition, root, loader);
        using var binding = new RuntimeBackgroundBinding(actor, host);
        var original = actor.CurrentContent!;
        Assert(original.LocalBounds == new Rect2(0, 0, 100, 50) && original.Coverage == BackgroundCoverage.Unknown,
            "Native bounds and conservative coverage must describe content before host fitting.");
        Assert(original.Root.GetParent<Node2D>().Scale == new Vector2(4, 4), "Runtime host did not fit native content.");
        host.Size = new Vector2(400, 100);
        Assert(original.Root.GetParent<Node2D>().Position == new Vector2(0, -50), "Runtime host did not refit on resize.");

        var candidate = await actor.PrepareBackgroundAppearanceAsync("night");
        var discarded = candidate.Target.Root;
        Assert(ReferenceEquals(actor.CurrentContent, original) && actor.Appearance == "day" && !discarded.Visible,
            "Preparation changed the committed background.");
        await candidate.DisposeAsync();
        await candidate.DisposeAsync();
        Assert(discarded.IsQueuedForDeletion(), "An uncommitted candidate was not released.");

        Action failingObserver = () => throw new InvalidOperationException("Injected host commit failure.");
        candidate = await actor.PrepareBackgroundAppearanceAsync("night");
        actor.ContentChanged += failingObserver;
        ExpectBackgroundFailure<InvalidOperationException>(candidate.Commit);
        actor.ContentChanged -= failingObserver;
        candidate.Rollback();
        candidate.Rollback();
        await candidate.DisposeAsync();
        Assert(ReferenceEquals(actor.CurrentContent, original) && actor.Appearance == "day" && original.Root.Visible,
            "A failing commit did not restore its source content and appearance.");

        loader.Failure = true;
        await ExpectBackgroundFailureAsync<InvalidOperationException>(() => actor.PrepareBackgroundAppearanceAsync("failed"));
        loader.Failure = false;
        Assert(ReferenceEquals(actor.CurrentContent, original), "Load failure changed current content.");

        loader.Pending = new TaskCompletionSource<Texture2D>();
        using (var cancellation = new CancellationTokenSource())
        {
            var loading = actor.PrepareBackgroundAppearanceAsync("cancelled", cancellation.Token);
            cancellation.Cancel();
            loader.Complete(texture);
            await ExpectBackgroundFailureAsync<OperationCanceledException>(() => loading);
        }
        Assert(ReferenceEquals(actor.CurrentContent, original), "Late cancellation changed current content.");

        loader.Pending = new TaskCompletionSource<Texture2D>();
        var staleLoad = actor.PrepareBackgroundAppearanceAsync("late");
        actor.SetAppearance("newer");
        var newer = actor.CurrentContent!;
        loader.Complete(texture);
        await ExpectBackgroundFailureAsync<ActorException>(() => staleLoad);
        Assert(ReferenceEquals(actor.CurrentContent, newer), "Late preparation overwrote a newer appearance.");

        candidate = await actor.PrepareBackgroundAppearanceAsync("unconfirmed");
        candidate.Commit();
        var unconfirmed = candidate.Target.Root;
        await candidate.DisposeAsync();
        Assert(ReferenceEquals(actor.CurrentContent, newer) && unconfirmed.IsQueuedForDeletion(),
            "Disposing an unconfirmed commit must roll back.");

        var gate = new StateMutationGate();
        actor.BindStateMutationGate(gate);
        candidate = await actor.PrepareBackgroundAppearanceAsync("frozen");
        candidate.Commit();
        using (await gate.FreezeAsync())
        {
            ExpectBackgroundFailure<StateRuntimeException>(candidate.Rollback);
            Assert(ReferenceEquals(actor.CurrentContent, candidate.Target) && !candidate.Target.Root.IsQueuedForDeletion(),
                "Rejected rollback released still-active content.");
        }
        candidate.Rollback();
        await candidate.DisposeAsync();

        using var retainedOld = newer.Retain();
        candidate = await actor.PrepareBackgroundAppearanceAsync("confirmed");
        candidate.Commit();
        using (newer.Retain()) { }
        candidate.ConfirmCommit();
        candidate.ConfirmCommit();
        await candidate.DisposeAsync();
        ExpectBackgroundFailure<ObjectDisposedException>(() => newer.Retain());
        Assert(!newer.Root.IsQueuedForDeletion(), "Confirmation released a source still leased by another consumer.");
        retainedOld.Dispose();
        Assert(newer.Root.IsQueuedForDeletion(), "The last old-content lease did not release its node.");

        var current = actor.CurrentContent!;
        var survivor = current.Retain();
        loader.Pending = new TaskCompletionSource<Texture2D>();
        var destroyedLoad = actor.PrepareBackgroundAppearanceAsync("after-destroy");
        Action destructionObserver = () => ExpectBackgroundFailure<ObjectDisposedException>(() => current.Retain());
        actor.ContentChanged += destructionObserver;
        actor.Destroy();
        actor.Destroy();
        loader.Complete(texture);
        await ExpectBackgroundFailureAsync<ObjectDisposedException>(() => destroyedLoad);
        ExpectBackgroundFailure<ObjectDisposedException>(() => current.Retain());
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(IsInstanceValid(current.Root) && current.Root.GetParent() is null && !IsInstanceValid(root),
            "Destroy must detach leased content before freeing the actor lifecycle root.");
        survivor.Dispose();
        survivor.Dispose();
        binding.Dispose();
        host.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(current.Root), "The last survivor lease did not release destroyed actor content.");

        await ValidateBackgroundResourceScopesAsync();
    }

    private async Task ValidateBackgroundResourceScopesAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Actors!.Actors.Add(CreateActorDefinition("leased-background", ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("day", "res://tests/headless/fixtures/background_dark.svg"),
            ("night", "res://tests/headless/fixtures/background_light.svg")));
        var bootstrap = new MajoRuntimeBootstrap { Configuration = configuration };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var resources = bootstrap.Host.Services.Get<IResourceManager>();
        var baseline = resources.CaptureCacheSnapshot().ActiveLeaseCount;
        var actor = (IBackgroundActor)await manager.CreateAsync("leased-background");
        manager.SetBackgroundSlot(actor, "aux");
        Assert(manager.GetBackgroundSlot(actor) == "aux", "Manager did not own the background slot.");
        actor.SetAppearance("night");
        var contributor = new ActorStateContributor(manager);
        var saved = await contributor.CaptureAsync(default);
        manager.SetBackgroundSlot(actor, "main");
        actor.SetAppearance("day");
        var context = new RestoreContext(contributor.Scope, new Dictionary<string, StateRecord> { [contributor.Key] = saved });
        await using (var restore = await contributor.PrepareRestoreAsync(saved, context, default))
        {
            Assert(manager.GetBackgroundSlot(actor) == "main" && actor.Appearance == "day",
                "Preparing restoration modified the active background or its slot.");
            restore.Commit();
        }
        actor = manager.GetActor<IBackgroundActor>("leased-background");
        Assert(manager.GetBackgroundSlot(actor) == "aux" && actor.Appearance == "night",
            "Background restoration did not restore the manager-owned slot and prepared appearance.");
        var content = actor.CurrentContent!;
        var lease = content.Retain();
        for (var index = 0; index < 100; index++)
        {
            await using var prepared = await actor.PrepareBackgroundAppearanceAsync(index % 2 == 0 ? "night" : "day");
            prepared.Commit();
            prepared.ConfirmCommit();
        }
        Assert(resources.CaptureCacheSnapshot().ActiveLeaseCount == baseline + 2,
            "Repeated switches must retain only the current content and the externally leased source.");
        manager.Remove(actor.Id);
        Assert(resources.CaptureCacheSnapshot().ActiveLeaseCount == baseline + 1,
            "Actor destruction released a leased source resource or retained unused content.");
        lease.Dispose();
        Assert(resources.CaptureCacheSnapshot().ActiveLeaseCount == baseline, "The last content lease did not release its resource scope.");
        // Freeing the containing runtime must also detach outstanding leases before Godot frees children.
        actor = (IBackgroundActor)await manager.CreateAsync("leased-background");
        content = actor.CurrentContent!;
        lease = content.Retain();
        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(IsInstanceValid(content.Root), "Runtime tree teardown freed externally leased content.");
        lease.Dispose();
    }

    private static void ExpectBackgroundFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ExpectBackgroundFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class BackgroundProbeLoader(Texture2D texture) : IActorTextureLoader
    {
        public TaskCompletionSource<Texture2D>? Pending { get; set; }
        public bool Failure { get; set; }
        public Texture2D Load(string path) => Failure ? throw new InvalidOperationException("Injected load failure.") : texture;
        public Task<Texture2D> LoadAsync(ResourceKey key, string? overridePath = null, CancellationToken cancellationToken = default) =>
            Pending?.Task ?? Task.FromResult(Load(overridePath ?? key.Path));
        public void Complete(Texture2D value)
        {
            var pending = Pending!;
            Pending = null;
            pending.SetResult(value);
        }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

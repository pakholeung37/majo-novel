#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Native content only. The presentation host owns fitting and slot placement.</summary>
public abstract class GodotBackgroundActorBase : GodotActorBase, IBackgroundActor
{
    private readonly HashSet<GodotBackgroundContent> contents = new();
    private GodotBackgroundContent? current;
    private PreparedAppearance? unconfirmed;

    protected GodotBackgroundActorBase(ActorDefinition definition, Node2D container, string initialAppearance)
        : base(definition, container, initialAppearance)
    {
        Container.TreeExiting += Destroy;
    }

    protected abstract GodotBackgroundContent LoadContent(string appearance);
    protected abstract Task<GodotBackgroundContent> LoadContentAsync(string appearance, CancellationToken token);

    private Node2D Container => (Node2D)View;
    public IBackgroundContent? CurrentContent => current;
    public event Action? ContentChanged;
    public override Vector2 Position => Container.Position;
    protected override void ApplyPosition(Vector2 position) => Container.Position = position;

    public override void SetAppearance(string appearance) => PersistentWrite(() =>
    {
        EnsurePreparable(appearance);
        var sourceLease = current?.Retain();
        GodotBackgroundContent target;
        try { target = LoadContent(appearance); }
        catch { sourceLease?.Dispose(); throw; }
        var prepared = new PreparedAppearance(this, target, appearance, current, sourceLease, AppearanceEpoch);
        try { prepared.Commit(); prepared.ConfirmCommit(); }
        finally { prepared.Dispose(); }
    });

    public async Task<IPreparedBackgroundAppearance> PrepareBackgroundAppearanceAsync(
        string resolvedAppearance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePreparable(resolvedAppearance);
        var epoch = AppearanceEpoch;
        var source = current;
        var sourceLease = source?.Retain();
        GodotBackgroundContent? target = null;
        try
        {
            target = await LoadContentAsync(resolvedAppearance, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAlive();
            if (epoch != AppearanceEpoch) throw new ActorException("Background changed while its appearance was loading.");
            return new PreparedAppearance(this, target, resolvedAppearance, source, sourceLease, epoch);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try { target?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            try { sourceLease?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 1) throw new AggregateException("Background preparation and cleanup failed.", failures);
            throw;
        }
    }

    public override async Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default)
    {
        await using var prepared = await PrepareBackgroundAppearanceAsync(appearance, cancellationToken);
    }

    private void EnsurePreparable(string appearance)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        if (unconfirmed is not null) throw new ActorException("Confirm or roll back the current background commit before preparing another.");
    }

    protected GodotBackgroundContent OwnTextureContent(Texture2D texture, IDisposable? resources = null)
    {
        GodotBackgroundContent? content = null;
        try
        {
            content = GodotBackgroundContent.FromTexture(texture, resources, () => contents.Remove(content!));
            content.Root.Visible = false;
            Container.AddChild(content.Root);
            contents.Add(content);
            return content;
        }
        catch
        {
            if (content is not null) content.Dispose();
            else resources?.Dispose();
            throw;
        }
    }

    protected GodotBackgroundContent OwnContent(CanvasItem root, Rect2 bounds, BackgroundCoverage coverage, IDisposable? resources = null)
    {
        GodotBackgroundContent? content = null;
        try
        {
            root.Visible = false;
            Container.AddChild(root);
            content = new GodotBackgroundContent(root, bounds, coverage, resources, () => contents.Remove(content!));
            contents.Add(content);
            return content;
        }
        catch { root.QueueFree(); resources?.Dispose(); throw; }
    }

    protected override void OnDestroy()
    {
        if (GodotObject.IsInstanceValid(Container)) Container.TreeExiting -= Destroy;
        current = null;
        unconfirmed = null;
        var failures = new List<Exception>();
        foreach (var content in contents.ToArray())
        {
            try { content.DetachFrom(Container); }
            catch (Exception error) { failures.Add(error); }
            try { content.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        try { ContentChanged?.Invoke(); }
        catch (Exception error) { failures.Add(error); }
        ContentChanged = null;
        if (failures.Count > 0) throw new AggregateException("Background destruction failed.", failures);
    }

    private sealed class PreparedAppearance : IPreparedBackgroundAppearance
    {
        private readonly GodotBackgroundActorBase actor;
        private readonly GodotBackgroundContent target;
        private readonly GodotBackgroundContent? source;
        private readonly string sourceAppearance;
        private readonly long epoch;
        private IDisposable? sourceLease;
        private bool committed;
        private bool confirmed;
        private bool rolledBack;
        private bool disposed;

        public PreparedAppearance(GodotBackgroundActorBase actor, GodotBackgroundContent target, string appearance,
            GodotBackgroundContent? source, IDisposable? sourceLease, long epoch)
        {
            this.actor = actor;
            this.target = target;
            this.source = source;
            this.sourceLease = sourceLease;
            this.epoch = epoch;
            sourceAppearance = actor.Appearance;
            ResolvedAppearance = appearance;
        }

        public string ResolvedAppearance { get; }
        public IBackgroundContent Target => target;

        public void Commit() => actor.PersistentWrite(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            actor.EnsureAlive();
            if (committed || rolledBack || actor.AppearanceEpoch != epoch || !ReferenceEquals(actor.current, source))
                throw new ActorException("Background appearance preparation is stale or already committed.");
            // Mark first so a failing host callback can still be rolled back.
            committed = true;
            actor.unconfirmed = this;
            actor.current = target;
            actor.Appearance = ResolvedAppearance;
            actor.InvalidateAppearance();
            if (source is not null && actor.Container.IsAncestorOf(source.Root)) source.Root.Visible = false;
            target.Root.Visible = true;
            actor.ContentChanged?.Invoke();
        });

        public void ConfirmCommit()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (confirmed) return;
            if (!committed || rolledBack || !actor.IsAlive || !ReferenceEquals(actor.unconfirmed, this))
                throw new ActorException("Background appearance is not awaiting confirmation.");
            confirmed = true;
            actor.unconfirmed = null;
            try { source?.Dispose(); }
            finally { ReleaseSourceLease(); }
        }

        public void Rollback()
        {
            if (rolledBack) return;
            if (confirmed) throw new ActorException("A confirmed background appearance cannot be rolled back.");
            var failures = new List<Exception>();
            try
            {
                if (committed && actor.IsAlive && ReferenceEquals(actor.unconfirmed, this))
                    actor.PersistentWrite(() =>
                    {
                        rolledBack = true;
                        actor.current = source;
                        actor.Appearance = sourceAppearance;
                        actor.unconfirmed = null;
                        actor.InvalidateAppearance();
                        target.Root.Visible = false;
                        if (source is not null) source.Root.Visible = true;
                        actor.ContentChanged?.Invoke();
                    });
            }
            catch (Exception error)
            {
                // A rejected write never changed state: retain everything for a later retry.
                if (!rolledBack) throw;
                failures.Add(error);
            }
            rolledBack = true;
            try { target.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            try { ReleaseSourceLease(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Background rollback failed.", failures);
        }

        private void ReleaseSourceLease()
        {
            var lease = sourceLease;
            sourceLease = null;
            lease?.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            try { if (!confirmed) Rollback(); }
            finally
            {
                // If the mutation gate rejected rollback, Dispose can be retried.
                if (confirmed || rolledBack) { disposed = true; ReleaseSourceLease(); }
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionStageBackend
{
    internal IBackgroundActor? GetBackgroundActor(StageId stage) => backgrounds.GetValueOrDefault(stage)?.Actor;

    private sealed class PreparedBackdrop : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private GodotDirectionRenderHost Host => backend.host;
        private readonly GodotDirectionStageSurface surface;
        private readonly GodotDirectionBackdropBinding? sourceBinding;
        private GodotDirectionBackdropBinding? targetBinding;
        private readonly GodotBackgroundRaster? sourceRaster;
        private GodotBackgroundRaster? targetRaster;
        private IPreparedBackgroundAppearance? appearance;
        private readonly bool forceAppearance;
        private readonly long sourceLocale;
        private readonly long targetLocale;
        private readonly BackdropState? source;
        private readonly BackdropState? target;
        private readonly DirectionTransitionSpec transition;
        private readonly TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
        private GodotDirectionRenderHost.PreparedBackdropVisualTransition? driver;
        private bool committed, confirmed, rolledBack, disposed, visualFinished;
        private long generation;
        private bool Animate => transition.DurationSeconds > 0 && transition.Kind != DirectionTransitionId.Cut &&
            (source is not null || target is not null);
        public Task Settlement => settlement.Task;

        public PreparedBackdrop(GodotDirectionStageBackend backend, GodotDirectionStageSurface surface,
            GodotDirectionBackdropBinding? sourceBinding, BackdropState? target, DirectionTransitionSpec transition, bool forceAppearance)
        {
            this.backend = backend; this.surface = surface; this.sourceBinding = sourceBinding;
            sourceRaster = sourceBinding?.Current; source = surface.PublishedBackdrop;
            this.target = target; this.transition = transition;
            sourceLocale = sourceBinding?.LocaleRevision ?? 0; targetLocale = backend.LocaleRevision;
            this.forceAppearance = forceAppearance || sourceLocale != targetLocale;
        }

        public async Task PrepareAsync(CancellationToken token)
        {
            if (target is not null)
            {
                if (sourceBinding is not null && source?.DefinitionId == target.DefinitionId && source.FactoryId == target.FactoryId)
                    targetBinding = sourceBinding;
                else targetBinding = await GodotDirectionBackdropBinding.CreateAsync(surface.Canvas, surface.Viewport.Size,
                    target, backend.createActorManager ?? throw new DirectionException("Backgrounds require an actor manager factory."), token);
                var actor = targetBinding.Actor;
                if (ReferenceEquals(targetBinding, sourceBinding) && actor.Appearance == target.Appearance && !forceAppearance)
                    targetRaster = sourceRaster;
                else
                {
                    IBackgroundContent content;
                    if (!ReferenceEquals(targetBinding, sourceBinding) && actor.Appearance == target.Appearance && actor.CurrentContent is not null)
                        content = actor.CurrentContent;
                    else
                    {
                        appearance = await actor.PrepareBackgroundAppearanceAsync(target.Appearance, token);
                        content = appearance.Target;
                        if (appearance.ResolvedAppearance != target.Appearance)
                            throw new DirectionException("A background preparation must preserve the resolved full appearance.");
                    }
                    token.ThrowIfCancellationRequested();
                    GodotBackgroundRaster.ValidateTree(actor.SceneNode);
                    if (content.Root.GetParent() is not null && !targetBinding.Root.IsAncestorOf(content.Root))
                        throw new DirectionException("Background content was mounted outside its factory environment.");
                    if (ReferenceEquals(targetBinding, sourceBinding) && ReferenceEquals(content.Root, actor.CurrentContent?.Root))
                        throw new DirectionException("Appearance changes require an independently mountable candidate content root.");
                    targetRaster = new GodotBackgroundRaster(targetBinding.Root, content);
                }
            }
            // Ready inputs remain live throughout the transition; each Stage simulates content once.
            if (targetRaster is not null) await targetRaster.ReadyAsync(token);
            else if (sourceRaster is not null) await sourceRaster.ReadyAsync(token);
            token.ThrowIfCancellationRequested();
            if (Animate)
            {
                driver = Host.PrepareBackdropVisualTransition(surface.Id, transition, backend.drivers.GetRequired(transition.Kind), activate: false);
            }
        }

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed) throw new InvalidOperationException("Background is no longer preparable.");
            committed = true;
            generation = Host.BeginStageGeneration(surface.Id);
            backend.activeBackdrops[surface.Id] = this;
            appearance?.Commit();
            // The host owns visibility of leased offscreen inputs, including retired appearances.
            sourceRaster?.SetLiveVisible(true);
            targetRaster?.SetLiveVisible(true);
            if (targetBinding is not null)
            {
                targetBinding.Current = targetRaster;
                targetBinding.LocaleRevision = targetLocale;
                backend.backgrounds[surface.Id] = targetBinding;
            }
            else backend.backgrounds.Remove(surface.Id);
            Host.ClearBackdropTransition(surface.Id);
            Host.PublishBackdrop(surface.Id, target, targetRaster);
            if (!Animate) { Complete(); return; }
            if (source is not null && sourceRaster is not null)
                Host.PublishBackdropTransition(surface.Id, source, sourceRaster);
            driver!.Activate();
            clock = Host.Animate(transition, backend.easings.GetRequired(transition.Easing), progress =>
            {
                if (Host.IsCurrentStageGeneration(surface.Id, generation)) driver?.ApplyProgress(progress);
            }, Complete, error => settlement.TrySetException(error));
        }

        public void ConfirmCommit()
        {
            if (confirmed) return;
            appearance?.ConfirmCommit();
            confirmed = true;
            confirmation.TrySetResult(true);
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            confirmation.TrySetResult(false);
            var failures = new List<Exception>();
            try { clock?.Dispose(); clock = null; driver?.Dispose(); driver = null; }
            catch (Exception error) { failures.Add(error); }
            try { appearance?.Rollback(); }
            catch (Exception error) { failures.Add(error); }
            if (committed && Host.IsCurrentStageGeneration(surface.Id, generation))
            {
                if (sourceBinding is not null)
                { sourceBinding.Current = sourceRaster; sourceBinding.LocaleRevision = sourceLocale; backend.backgrounds[surface.Id] = sourceBinding; }
                else backend.backgrounds.Remove(surface.Id);
                try { Host.PublishBackdrop(surface.Id, source, sourceRaster); Host.ClearBackdropTransition(surface.Id); }
                catch (Exception error) { failures.Add(error); }
            }
            settlement.TrySetCanceled();
            if (failures.Count > 0) throw new AggregateException("Background rollback failed.", failures);
        }

        public ValueTask FinishAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Complete(); return ValueTask.CompletedTask; }

        private void Complete()
        {
            if (visualFinished || rolledBack || disposed) return;
            visualFinished = true;
            clock?.Dispose(); clock = null;
            Exception? failure = null;
            if (Host.IsCurrentStageGeneration(surface.Id, generation))
            {
                try { driver?.ApplyProgress(1); }
                catch (Exception error) { failure = error; }
                try { driver?.Dispose(); }
                catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
                driver = null;
                Host.PublishBackdrop(surface.Id, target, targetRaster);
                Host.ClearBackdropTransition(surface.Id);
            }
            if (failure is not null) { settlement.TrySetException(failure); return; }
            _ = SettleAsync();
        }

        private async Task SettleAsync()
        {
            try
            {
                if (!await confirmation.Task || rolledBack) return;
                if (target is null && Host.IsCurrentStageGeneration(surface.Id, generation))
                    await Host.ReleaseActorModifierTargetAsync(new Majo.Runtime.Direction.Actors.ActorFxTarget(surface.Id,
                        Majo.Runtime.Direction.Actors.ActorFxSlot.Backdrop), surface.ModifierTarget);
                settlement.TrySetResult();
            }
            catch (Exception error) { settlement.TrySetException(error); }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            var failures = new List<Exception>();
            if (committed && !confirmed && !rolledBack)
            { try { Rollback(default); } catch (Exception error) { failures.Add(error); } }
            if (confirmed && !rolledBack && !visualFinished)
            { try { Complete(); } catch (Exception error) { failures.Add(error); } }
            disposed = true;
            confirmation.TrySetResult(false);
            try { clock?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            try { driver?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            if (backend.activeBackdrops.GetValueOrDefault(surface.Id) == this) backend.activeBackdrops.Remove(surface.Id);
            var keptTarget = confirmed && !rolledBack;
            try
            {
                var retiredRaster = keptTarget ? sourceRaster : targetRaster;
                var retainedRaster = keptTarget ? targetRaster : sourceRaster;
                if (!ReferenceEquals(retiredRaster, retainedRaster)) retiredRaster?.Dispose();
            }
            catch (Exception error) { failures.Add(error); }
            try
            {
                var retiredBinding = keptTarget ? sourceBinding : targetBinding;
                var retainedBinding = keptTarget ? targetBinding : sourceBinding;
                if (retiredBinding is not null && !ReferenceEquals(retiredBinding, retainedBinding)) await retiredBinding.DisposeAsync();
            }
            catch (Exception error) { failures.Add(error); }
            try { if (appearance is not null) await appearance.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            if (failures.Count > 0) throw new AggregateException("Background transaction cleanup failed.", failures);
        }
    }
}

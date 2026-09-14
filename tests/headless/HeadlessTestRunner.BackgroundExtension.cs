#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Demos.BackgroundExtension;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private const string TestBackgroundFarPath = "res://tests/headless/fixtures/background_dark.svg";
    private const string TestBackgroundNearPath = "res://tests/headless/fixtures/background_light.svg";

    private async Task ValidateBackgroundExtensionAsync()
    {
        var initialRasters = GodotBackgroundRaster.LiveCount;
        var initialBindings = GodotDirectionBackdropBinding.Count;
        var configuration = CreateHeadlessConfiguration();
        configuration.Actors!.ReferenceResolution = new Vector2I(640, 360);
        configuration.Localization!.DefaultLocale = "en";
        configuration.Localization.SupportedLocales = ["en", "zh-CN"];
        configuration.Settings!.DialogueLocale = "en";
        foreach (var id in new[] { "composite", "other" })
            configuration.Actors.Actors.Add(new ActorDefinitionConfiguration { Id = id, FactoryId = CompositeBackgroundPlugin.FactoryId });
        var faults = new BackgroundFaultControl();
        configuration.Actors.Actors.Add(new ActorDefinitionConfiguration { Id = "faulty", FactoryId = "test.fault-background" });
        var bootstrap = new MajoRuntimeBootstrap
        {
            Configuration = configuration,
            ConfigureActorFactories = registry => registry.Register("test.fault-background", ActorFactoryDomains.Direction,
                context => new FaultBackgroundFactory(context.BackgroundRoot, context.Resources, faults),
                (configured, _) => new ActorDefinition(configured.Id, "test.fault-background")),
            ConfigureTransitionDrivers = registry => registry.Register(new FailingBackgroundDriver()),
        };
        bootstrap.AddPlugin(new CompositeBackgroundPlugin(TestBackgroundFarPath, TestBackgroundNearPath));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var services = bootstrap.Host!.Services;
        var direction = services.Get<IDirectionTransactionCoordinator>();
        var backend = services.Get<GodotDirectionStageBackend>();
        var host = services.Get<GodotDirectionRenderHost>();
        var resources = services.Get<IResourceManager>();
        var baselineLeases = resources.CaptureCacheSnapshot().ActiveLeaseCount;
        var measurements = new List<object>();
        void Measure(string phase) => measurements.Add(new
        {
            phase,
            liveContent = GodotBackgroundRaster.LiveCount - initialRasters,
            snapshots = GodotBackgroundRaster.SnapshotCount,
            bindings = GodotDirectionBackdropBinding.Count - initialBindings,
            activeLeases = resources.CaptureCacheSnapshot().ActiveLeaseCount,
        });
        Measure("baseline");
        BackdropState State(string appearance = "Day", string id = "composite") => new(id, CompositeBackgroundPlugin.FactoryId, appearance);
        BackdropPatch Patch(BackdropState state, double seconds = 0, StageId? stage = null) => new(stage ?? StageId.Main, state,
            seconds == 0 ? DirectionTransitionSpec.Cut : new DirectionTransitionSpec(DirectionTransitionId.Dissolve, seconds));
        try
        {
            var metadata = services.Get<DirectionActorDefinitionEnvironment>().Resolver;
            var preload = metadata.ResolveExplicitAppearance("composite", "Night");
            Assert(metadata.GetPreloadResources(new ActorSpawnRequest("main", CompositeBackgroundPlugin.FactoryId, preload.Appearance) { DefinitionId = "composite", RequireExplicitDefinition = true }).Count == 2,
                "Custom background metadata did not expose both textures.");
            await direction.ExecuteAsync(new[] { Patch(State()) }, DirectionCompletionBarrier.Settle);
            var actor = backend.GetBackgroundActor(StageId.Main)!;
            var first = actor.CurrentContent!;
            var raster = host.Backdrop.Raster;
            Assert(actor.FactoryId == CompositeBackgroundPlugin.FactoryId && first.Root is Node2D &&
                first.LocalBounds.Position == new Vector2(-80, -20), "The custom background did not create native Node2D content.");
            await direction.ExecuteAsync(new[] { Patch(State() with { Fit = BackdropFit.Contain, Tint = new DirectionColor(.8f, .7f, .6f, .75f) }) }, DirectionCompletionBarrier.Settle);
            Assert(ReferenceEquals(actor, backend.GetBackgroundActor(StageId.Main)) && ReferenceEquals(raster, host.Backdrop.Raster),
                "Layout-only background changes recreated the actor or content.");
            var mix = await direction.ExecuteAsync(new[] { Patch(State("Night"), 1) });
            Assert(ReferenceEquals(actor, backend.GetBackgroundActor(StageId.Main)) && actor.CurrentContent!.Root is Control &&
                actor.CurrentContent.Coverage == BackgroundCoverage.Unknown && GodotBackgroundRaster.SnapshotCount == 0,
                "Appearance change must reuse the actor and keep both content inputs live without snapshots.");
            Measure("transition");
            if (DisplayServer.GetName() != "headless")
            {
                foreach (var tween in GetTree().GetProcessedTweens()) { tween.Pause(); tween.CustomStep(.5); }
                var oldAccent = first.Root.GetChildren().OfType<AnimatedBackdropAccent>().Single();
                var newAccent = actor.CurrentContent!.Root.GetChildren().OfType<AnimatedBackdropAccent>().Single();
                var oldColor = oldAccent.Color;
                var newColor = newAccent.Color;
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport)) { }
                Assert(first.Root.Visible && oldAccent.Color != oldColor && newAccent.Color != newColor,
                    "Confirmed appearance replacement stopped one of its live content inputs.");
                var camera = await direction.ExecuteAsync(new IDirectionDirective[]
                {
                    new ViewPosePatch(ViewId.Main, default, 1.2f, default,
                        new DirectionTransitionSpec(DirectionTransitionId.Dissolve, 1)),
                });
                foreach (var tween in GetTree().GetProcessedTweens()) tween.Pause();
                Assert(host.GetRenderResourceMetrics().ViewCount == 2, "Overlapping camera dissolve did not retain a live old view.");
                // A newly created camera source must see the current background blend too.
                var liveCamera = host.LiveViewSources.Single();
                AssertViewRendering(liveCamera, true, "Committed live camera source");
                Assert(host.InspectViewDemand().All(view => view.Requests.Length == 0),
                    "Committed camera must transfer preparation demand to a dependency.");
                Assert(liveCamera.BackgroundOutput.Visible == false && liveCamera.TransitionBlendOutput.Visible,
                    "A camera created during background dissolve missed its blend inputs.");
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport)) { }
                await direction.FinishAsync(camera);
                Assert(host.GetRenderResourceMetrics().ViewCount == 1, "Overlapping camera retained its temporary view.");
                Assert(host.InspectViewDemand().All(view => view.Sources.Length == 0 && view.Requests.Length == 0),
                    "Finished camera transition retained render demand.");
            }
            await direction.FinishAsync(mix);
            Assert(GodotBackgroundRaster.SnapshotCount == 0 && resources.CaptureCacheSnapshot().ActiveLeaseCount == baselineLeases + 2,
                "Finished background transition retained snapshots or old texture leases.");

            if (DisplayServer.GetName() != "headless")
            {
                var directory = ProjectSettings.GlobalizePath("res://artifacts/background-actor/custom");
                Directory.CreateDirectory(directory);
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport)) frame.SavePng(Path.Combine(directory, "night-control.png"));
                var cancelState = direction.State;
                var cancelSource = actor.CurrentContent;
                var cancelPlan = new DirectionPlanner(metadata).Plan(cancelState, new[] { Patch(State()) }).DomainPlan;
                using var cancellation = new CancellationTokenSource();
                var preparing = backend.PrepareAsync(cancelPlan, StageId.Main, cancelState.Stages[StageId.Main], cancelPlan.TargetState.Stages[StageId.Main], cancellation.Token);
                cancellation.Cancel();
                await ExpectBackgroundFailureAsync<OperationCanceledException>(() => preparing);
                Assert(ReferenceEquals(cancelSource, actor.CurrentContent), "Cancellation before the first GPU frame changed active content.");
            }
            var stable = direction.State;
            var originalContent = actor.CurrentContent;
            async Task ProbeRollbackAsync(string id)
            {
                var plan = new DirectionPlanner(metadata).Plan(stable, new[] { Patch(State("Day", id)) }).DomainPlan;
                await using var component = await backend.PrepareAsync(plan, StageId.Main, stable.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main], default);
                var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
                component.Commit(token);
                await component.FinishAsync(default);
                Assert(!component.Settlement.IsCompleted && IsInstanceValid(originalContent!.Root),
                    "Zero-duration completion retired the source before transaction confirmation.");
                component.Rollback(token);
                Assert(ReferenceEquals(actor, backend.GetBackgroundActor(StageId.Main)) && ReferenceEquals(originalContent, actor.CurrentContent),
                    "Background rollback did not restore the original binding and content.");
            }
            await ProbeRollbackAsync("composite");
            await ProbeRollbackAsync("other");

            var failedPlan = new DirectionPlanner(metadata).Plan(stable, new[] { Patch(State()) }).DomainPlan;
            await using (var component = await backend.PrepareAsync(failedPlan, StageId.Main, stable.Stages[StageId.Main], failedPlan.TargetState.Stages[StageId.Main], default))
            {
                Action fault = () => throw new InvalidOperationException("Injected background commit callback failure.");
                actor.ContentChanged += fault;
                ExpectBackgroundFailure<InvalidOperationException>(() => component.Commit(new DirectionCommitToken(failedPlan.Id, failedPlan.TargetState.Revision)));
                actor.ContentChanged -= fault;
                component.Rollback(default);
            }
            Assert(ReferenceEquals(originalContent, actor.CurrentContent), "Commit failure lost old background content.");

            var second = new StageId("second-background");
            await direction.ExecuteAsync(new IDirectionDirective[] { new StageCreatePatch(second, new ViewId("second-background"), DirectionTransitionSpec.Cut), Patch(State(), stage: second) }, DirectionCompletionBarrier.Settle);
            Assert(!ReferenceEquals(actor, backend.GetBackgroundActor(second)) &&
                resources.CaptureCacheSnapshot().ActiveLeaseCount == baselineLeases + 4,
                "Two stages must own independent instances and resource leases.");
            if (DisplayServer.GetName() != "headless")
            {
                host.PublishScreen(new ScreenState(new LayoutId("split"), new Dictionary<LayoutSlotId, ViewId>
                { [LayoutSlotId.Main] = ViewId.Main, [new("inset")] = new ViewId("second-background") }));
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                    frame.SavePng(ProjectSettings.GlobalizePath("res://artifacts/background-actor/custom/two-stages.png"));
                host.PublishScreen(ScreenState.Default);
            }
            var capture = direction.State;
            var contributor = new DirectionStateContributor(direction);
            var saved = await contributor.CaptureAsync(default);
            Assert(saved.SchemaVersion == 6, "Custom background state was saved using an old schema.");
            await direction.ExecuteAsync(new[] { Patch(State()) }, DirectionCompletionBarrier.Settle);
            var restoreContext = new RestoreContext(contributor.Scope, new Dictionary<string, StateRecord> { [contributor.Key] = saved });
            await using (var restore = await contributor.PrepareRestoreAsync(saved, restoreContext, default)) restore.Commit();
            Assert(direction.State.Stages[StageId.Main].Backdrop == capture.Stages[StageId.Main].Backdrop,
                "Custom backdrop identity, appearance or layout failed to round-trip.");
            var beforeFailedRestore = direction.State;
            var activeBeforeFailure = backend.GetBackgroundActor(StageId.Main);
            var invalidStages = beforeFailedRestore.Stages.ToDictionary(pair => pair.Key, pair => pair.Value);
            invalidStages[second] = invalidStages[second] with { Backdrop = State("Day") with { FactoryId = ActorFactoryIds.Background } };
            await ExpectBackgroundFailureAsync<ActorFactoryConflictException>(() => ((DirectionTransactionCoordinator)direction).PrepareReplacementAsync(beforeFailedRestore with { Stages = invalidStages }, default));
            Assert(ReferenceEquals(activeBeforeFailure, backend.GetBackgroundActor(StageId.Main)) && direction.State.Revision == beforeFailedRestore.Revision && direction.State.Stages[StageId.Main].Backdrop == beforeFailedRestore.Stages[StageId.Main].Backdrop,
                "Factory mismatch during restore changed the active background.");

            var leasesBeforeFault = resources.CaptureCacheSnapshot().ActiveLeaseCount;
            var liveBeforeFault = GodotBackgroundRaster.LiveCount;
            var firstBeforeFault = backend.GetBackgroundActor(StageId.Main)!.CurrentContent;
            var secondBeforeFault = backend.GetBackgroundActor(second)!.CurrentContent;
            var failingStages = beforeFailedRestore.Stages.ToDictionary(pair => pair.Key, pair => pair.Value);
            failingStages[StageId.Main] = failingStages[StageId.Main] with { Backdrop = State("Day") };
            failingStages[second] = failingStages[second] with { Backdrop = new BackdropState("faulty", "test.fault-background", "Broken") };
            await ExpectBackgroundFailureAsync<InvalidOperationException>(() => ((DirectionTransactionCoordinator)direction).PrepareReplacementAsync(
                beforeFailedRestore with { Stages = failingStages }, default));
            Assert(ReferenceEquals(firstBeforeFault, backend.GetBackgroundActor(StageId.Main)!.CurrentContent) &&
                ReferenceEquals(secondBeforeFault, backend.GetBackgroundActor(second)!.CurrentContent) &&
                leasesBeforeFault == resources.CaptureCacheSnapshot().ActiveLeaseCount && liveBeforeFault == GodotBackgroundRaster.LiveCount,
                "Failure preparing the second Stage leaked candidates or changed the first Stage.");
            await ExpectBackgroundFailureAsync<InvalidOperationException>(() => direction.ExecuteAsync(new[]
            {
                new BackdropPatch(StageId.Main, State("Day"), new DirectionTransitionSpec(new("test-failed-background-driver"), 1)),
            }));
            Assert(ReferenceEquals(firstBeforeFault, backend.GetBackgroundActor(StageId.Main)!.CurrentContent) && GodotBackgroundRaster.SnapshotCount == 0,
                "Driver preparation failure changed live content or leaked snapshots.");
            var faultState = new BackdropState("faulty", "test.fault-background", "Day");
            await direction.ExecuteAsync(new[] { Patch(faultState, stage: second) }, DirectionCompletionBarrier.Settle);
            var faultSource = backend.GetBackgroundActor(second)!.CurrentContent;
            await ExpectBackgroundFailureAsync<DirectionException>(() => direction.ExecuteAsync(new[] { Patch(faultState with { Appearance = "Escape" }, stage: second) }));
            Assert(ReferenceEquals(faultSource, backend.GetBackgroundActor(second)!.CurrentContent), "An unsupported canvas layer replaced active content.");
            var slowSource = backend.GetBackgroundActor(second)!.CurrentContent;
            var slowBefore = direction.State;
            var slowPlan = new DirectionPlanner(metadata).Plan(slowBefore, new[] { Patch(faultState with { Appearance = "Slow" }, stage: second) }).DomainPlan;
            using (var cancellation = new CancellationTokenSource())
            {
                var slow = backend.PrepareAsync(slowPlan, second, slowBefore.Stages[second], slowPlan.TargetState.Stages[second], cancellation.Token);
                Assert(faults.Loading, "Slow background did not reach the injected load boundary.");
                cancellation.Cancel();
                faults.Release.TrySetResult();
                await ExpectBackgroundFailureAsync<OperationCanceledException>(() => slow);
            }
            Assert(ReferenceEquals(slowSource, backend.GetBackgroundActor(second)!.CurrentContent),
                "Late cancelled content replaced the stable background.");
            await direction.ExecuteAsync(new[] { Patch(State(), stage: second) }, DirectionCompletionBarrier.Settle);
            var localeContent = backend.GetBackgroundActor(StageId.Main)!.CurrentContent;
            var locales = services.Get<ILocaleManager>();
            await locales.ChangeDialogueLocaleAsync(locales.DialogueLocale == "en" ? "zh-CN" : "en");
            await services.Get<DirectionBackgroundLocaleRefresh>().Pending;
            Assert(!ReferenceEquals(localeContent, backend.GetBackgroundActor(StageId.Main)!.CurrentContent),
                "Locale refresh bypassed prepared background content.");
            for (var index = 0; index < 100; index++)
                await direction.ExecuteAsync(new[] { Patch(State(index % 2 == 0 ? "Day" : "Night")) }, DirectionCompletionBarrier.Settle);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(GodotBackgroundRaster.LiveCount == initialRasters + 2 && GodotBackgroundRaster.SnapshotCount == 0 &&
                GodotDirectionBackdropBinding.Count == initialBindings + 2 && resources.CaptureCacheSnapshot().ActiveLeaseCount == baselineLeases + 4,
                "Repeated custom appearance changes leaked content surfaces, instances, snapshots or resources.");
            Measure("after-100-switches");
            await direction.ExecuteAsync(new IDirectionDirective[] { new StageClearPatch(second, DirectionTransitionSpec.Cut) }, DirectionCompletionBarrier.Settle);
            Assert(backend.GetBackgroundActor(second) is null, "Stage deletion retained its custom background actor.");
        }
        finally
        {
            await bootstrap.RequestShutdownAsync();
            bootstrap.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Assert(GodotBackgroundRaster.LiveCount == initialRasters && GodotBackgroundRaster.SnapshotCount == 0 &&
            GodotDirectionBackdropBinding.Count == initialBindings && resources.CaptureCacheSnapshot().ActiveLeaseCount == 0,
            "Runtime shutdown leaked custom background resources.");
        Measure("shutdown");
        var evidence = ProjectSettings.GlobalizePath("res://artifacts/background-actor/custom");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, $"metrics-{DisplayServer.GetName()}.json"),
            System.Text.Json.JsonSerializer.Serialize(measurements));
    }
}

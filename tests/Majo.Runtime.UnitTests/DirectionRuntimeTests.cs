using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Resources;
using Majo.Runtime.State;
using System.Runtime.CompilerServices;
using System.Text.Json;
using YarnSpinnerGodot;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionRuntimeTests
{
    [Theory]
    [InlineData(DirectionEase.BackIn, DirectionEaseCurve.Back, DirectionEaseMode.In)]
    [InlineData(DirectionEase.BackOut, DirectionEaseCurve.Back, DirectionEaseMode.Out)]
    [InlineData(DirectionEase.BackInOut, DirectionEaseCurve.Back, DirectionEaseMode.InOut)]
    [InlineData(DirectionEase.CubicOut, DirectionEaseCurve.Cubic, DirectionEaseMode.Out)]
    public void DirectionEaseDecomposesIntoCurveAndMode(
        DirectionEase ease,
        DirectionEaseCurve curve,
        DirectionEaseMode mode)
    {
        Assert.Equal(new DirectionEaseParts(curve, mode), ease.Decompose());
    }

    [Fact]
    public void NodeDirectionCompilerBuildsNodeLocalSymbolsAndTypedPreloadReferences()
    {
        var compiler = new NodeDirectionCompiler();
        var snapshot = new YarnSourceSnapshot(
            "test",
            "test.yarnproject",
            1,
            "en",
            new[]
            {
                new YarnSourceFileSnapshot(
                    "main.yarn",
                    "main.yarn",
                    "title: Opening\n---\n<<backdrop Background_001>>\n<<shot pose view:reaction stage:cell>>\n<<shot cg Still_001>>\n<<layout split main:main inset:reaction>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(compiler.Compile(snapshot));
        Assert.Equal("Opening", module.NodeId);
        Assert.Contains("cell", module.Symbols.Stages);
        Assert.Contains("reaction", module.Symbols.Views);
        Assert.Contains(module.Resources, resource =>
            resource.Kind == DirectionResourceKind.Backdrop &&
            resource.SemanticId == "Background_001");
        Assert.Contains(module.Resources, resource =>
            resource.Kind == DirectionResourceKind.Cg &&
            resource.SemanticId == "Still_001");
        Assert.Equal(4, module.SourceMaps.Count);
    }

    [Fact]
    public void NodeDirectionCompilerRetainsVersionAndNodeLocalMarks()
    {
        var snapshot = new YarnSourceSnapshot(
            "test", "test.yarnproject", 1, "en",
            new[]
            {
                new YarnSourceFileSnapshot(
                    "main.yarn", "main.yarn",
                    "title: Opening\n---\n<<mark focus at:25,75>>\n<<place Ema at:focus>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(new NodeDirectionCompiler().Compile(snapshot, 42));

        Assert.Equal(42, module.Version);
        var mark = Assert.Single(module.MarkDefinitions);
        Assert.Equal("focus", mark.Name);
        Assert.Equal(new DirectionPoint(25, 75), mark.Position);
        Assert.Contains("focus", module.Symbols.Marks);
    }

    [Fact]
    public void NodeDirectionCompilerCollectsExplicitImportsAndAuthoringDiagnostics()
    {
        var compiler = new NodeDirectionCompiler();
        var snapshot = new YarnSourceSnapshot(
            "test", "test.yarnproject", 1, "en",
            new[]
            {
                new YarnSourceFileSnapshot(
                    "main.yarn", "main.yarn",
                    "title: Opening\n---\n<<import stage news from Broadcast:broadcast>>\n<<backdrop>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(compiler.Compile(snapshot));
        var import = Assert.Single(module.Imports);
        Assert.Equal("stage", import.Kind);
        Assert.Equal("news", import.Alias);
        Assert.Equal("Broadcast", import.SourceNode);
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Code == "DIR001");
    }

    [Fact]
    public void DirectionMetadataRoundTripsNodeModules()
    {
        var module = new NodeDirectionModule(
            "Opening", 7,
            new DirectionSymbolTable(
                new HashSet<string> { "cell" },
                new HashSet<string> { "reaction" },
                new HashSet<string> { "wide" },
                new HashSet<string> { "focus" }),
            new[]
            {
                new TypedDirectionResourceReference(
                    DirectionResourceKind.Cg, "Still_001", "main.yarn", 4, 1),
            },
            new[] { new DirectionSourceMap("shot", "main.yarn", 4, 1) },
            new[]
            {
                new DirectionImport("stage", "cell", "Broadcast", "main", "main.yarn", 3, 1),
            },
            new[] { new DirectionMark("focus", new DirectionPoint(25, 75), "main.yarn", 5, 1) },
            Array.Empty<YarnCompilationDiagnostic>());
        var project = (YarnProject)RuntimeHelpers.GetUninitializedObject(typeof(YarnProject));
        project.CompiledDirectionModulesJson = YarnProjectDirectionMetadata.Serialize(new[] { module });

        var roundTrip = Assert.Single(YarnProjectDirectionMetadata.Deserialize(project));
        Assert.Equal(module.NodeId, roundTrip.NodeId);
        Assert.Equal(module.Version, roundTrip.Version);
        Assert.Equal(module.Symbols.Stages, roundTrip.Symbols.Stages);
        Assert.Equal(module.Resources, roundTrip.Resources);
        Assert.Equal(module.Imports, roundTrip.Imports);
        Assert.Equal(module.MarkDefinitions, roundTrip.MarkDefinitions);
    }
    [Fact]
    public void DirectionScopeQualifiesNodeLocalSymbolsAndResolvesExplicitImports()
    {
        var scopes = new DirectionScopeResolver();

        var first = scopes.ResolveStage("Adv02", "broadcast");
        var second = scopes.ResolveStage("Trial01", "broadcast");

        Assert.NotEqual(first, second);
        Assert.Equal(StageId.Main, scopes.ResolveStage("Adv02", "main"));
        Assert.Equal(first, scopes.ResolveStage("Adv02", "broadcast"));

        scopes.ImportStage("Trial01", "news", "Adv02", "broadcast");
        Assert.Equal(first, scopes.ResolveStage("Trial01", "news"));
        Assert.Throws<DirectionReferenceException>(() =>
            scopes.ImportStage("Trial01", "news", "Adv03", "broadcast"));
    }

    [Fact]
    public void DirectionScopeKeepsShotAliasesNodeLocal()
    {
        var scopes = new DirectionScopeResolver();
        var patch = new ViewPatch(
            ViewId.Main,
            default,
            default,
            DirectionValue<float>.From(1.5f),
            default,
            DirectionTransitionSpec.Cut);
        scopes.EnterNode("Opening");
        scopes.DefineShot("Opening", "wide", patch);
        Assert.Equal(patch, scopes.ResolveShot("Opening", "wide"));
        Assert.Throws<DirectionReferenceException>(() => scopes.DefineShot("Opening", "wide", patch));
        scopes.ExitNode("Opening");
        Assert.Throws<DirectionReferenceException>(() => scopes.ResolveShot("Opening", "wide"));

        scopes.EnterNode("Other");
        Assert.Throws<DirectionReferenceException>(() => scopes.ResolveShot("Other", "wide"));
    }

    [Fact]
    public void BeatBeginAllowsAnonymousBeats()
    {
        var registry = new RuntimeCommandRegistry();
        registry.Register<BeatBeginDirectionCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(registry.Registrations).Descriptor;
        var arguments = new CommandBinder(new CommandBindingContext(0.35))
            .Bind<BeatBeginDirectionCommandArguments>(descriptor, Array.Empty<string>());

        Assert.Null(arguments.Id);
    }

    [Fact]
    public void DirectionEaseAcceptsLegacyBackCurves()
    {
        var registry = new RuntimeCommandRegistry();
        registry.Register<ShotPoseDirectionCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(registry.Registrations).Descriptor;
        var arguments = new CommandBinder(new CommandBindingContext(0.35))
            .Bind<ShotPoseDirectionCommandArguments>(
                descriptor,
                new[] { "center:44.438,71.950", "over:0.4", "ease:back-out" });

        Assert.Equal("back-out", arguments.Ease);
    }

    [Fact]
    public void DirectionScopeReapplyingModuleReplacesHotReloadDefinitions()
    {
        var scopes = new DirectionScopeResolver();
        var first = new NodeDirectionModule(
            "Opening", 1,
            new DirectionSymbolTable(
                new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                new HashSet<string> { "focus" }),
            Array.Empty<TypedDirectionResourceReference>(),
            Array.Empty<DirectionSourceMap>(),
            Array.Empty<DirectionImport>(),
            new[] { new DirectionMark("focus", new DirectionPoint(10, 20), "test", 1, 1) },
            Array.Empty<YarnCompilationDiagnostic>());
        var second = first with
        {
            Version = 2,
            MarkDefinitions = new[]
            {
                new DirectionMark("focus", new DirectionPoint(80, 90), "test", 1, 1),
            },
        };

        scopes.ApplyModule(first);
        Assert.Equal(new DirectionPoint(10, 20), scopes.ResolveMark("Opening", "focus"));
        scopes.ApplyModule(second);
        Assert.Equal(new DirectionPoint(80, 90), scopes.ResolveMark("Opening", "focus"));
    }

    [Fact]
    public void YarnProjectVersionStoreKeepsRunningVersionActiveWhileStagingCandidate()
    {
        var store = new YarnProjectVersionStore(new VersionStoreResourceManager(), "scripts");
        // YarnProject is a Godot Resource and constructing it requires a live
        // Godot runtime. This store test only needs identity, so use managed
        // uninitialized instances and never access Resource members.
        var runningProject = (YarnProject)RuntimeHelpers.GetUninitializedObject(typeof(YarnProject));
        var candidateProject = (YarnProject)RuntimeHelpers.GetUninitializedObject(typeof(YarnProject));
        var running = CreateVersion(1, runningProject);
        var candidate = CreateVersion(2, candidateProject);

        store.StageForNextSession(running);
        store.MarkRunning("test", runningProject);
        store.StageForNextSession(candidate);

        Assert.Same(runningProject, store.GetActiveVersion("test")?.Project);
        Assert.Same(runningProject, store.GetRunningVersion("test")?.Project);

        store.Activate(candidate);
        Assert.Same(candidateProject, store.GetActiveVersion("test")?.Project);
        Assert.Same(runningProject, store.GetRunningVersion("test")?.Project);
    }

    private static YarnProjectVersion CreateVersion(long generation, YarnProject project) =>
        new(
            generation,
            "test",
            $"source-{generation}",
            $"program-{generation}",
            project,
            new HashSet<string>(),
            new HashSet<string>(),
            new Dictionary<string, string>(),
            new HashSet<string>(),
            Array.Empty<YarnCompilationDiagnostic>(),
            DateTimeOffset.UtcNow);

    [Fact]
    public void ActorAddressDistinguishesTheSameActorAcrossStages()
    {
        var actor = new ActorId("Ema");

        Assert.NotEqual(
            new ActorAddress(new StageId("cell"), actor),
            new ActorAddress(new StageId("broadcast"), actor));
    }

    [Fact]
    public void DefaultStateProvidesMainStageViewAndScreen()
    {
        var state = DirectionState.CreateDefault();

        Assert.Contains(StageId.Main, state.Stages.Keys);
        var view = Assert.IsType<StageViewSource>(state.Views[ViewId.Main].Source);
        Assert.Equal(StageId.Main, view.StageId);
        Assert.Equal(ViewId.Main, state.Screen.Bindings[LayoutSlotId.Main]);
        Assert.Equal(0, state.Revision);
    }

    [Fact]
    public async Task DirectionStateContributorRestoresThroughAtomicCoordinatorCommit()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var contributor = new DirectionStateContributor(coordinator);
        var initial = await contributor.CaptureAsync(default);

        await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(
                new StageId("broadcast"),
                new BackdropState(new ResourceKey("Background_025_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);
        Assert.Contains(new StageId("broadcast"), coordinator.State.Stages.Keys);

        await contributor.PreflightAsync(initial, default);
        await contributor.RestoreAsync(initial, default);

        Assert.DoesNotContain(new StageId("broadcast"), coordinator.State.Stages.Keys);
        Assert.True(coordinator.State.Revision > 0);
    }

    [Fact]
    public async Task DirectionStateContributorRejectsSnapshotsMissingMainStageOrView()
    {
        var contributor = new DirectionStateContributor(
            CreateCoordinator(new FakeDirectionBackend { AutoSettle = true }));
        const string mainStage = "[{\"id\":\"main\",\"effects\":{\"tint\":{\"r\":1,\"g\":1,\"b\":1,\"a\":1}},\"actors\":[]}]";
        const string mainView = "[{\"id\":\"main\",\"source\":\"stage:main\",\"centerX\":50,\"centerY\":50,\"zoom\":1,\"rotation\":0}]";
        var missingStage = CreateDirectionRecord("[]", mainView);
        var missingView = CreateDirectionRecord(mainStage, "[]");

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            contributor.PreflightAsync(missingStage, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            contributor.PreflightAsync(missingView, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DirectionStateContributorAcceptsLegacySnapshotsWithoutEffects()
    {
        var contributor = new DirectionStateContributor(
            CreateCoordinator(new FakeDirectionBackend { AutoSettle = true }));
        var record = new StateRecord(
            BuiltInStateKeys.Direction,
            StateScope.Game,
            1,
            JsonDocument.Parse("""
                {
                  "stages":[{"id":"main","backdrop":null,"actors":[]}],
                  "views":[{"id":"main","source":"stage:main","centerX":50,"centerY":50,"zoom":1,"rotation":0}],
                  "layout":"single",
                  "bindings":{"main":"main"},
                  "revision":0
                }
                """).RootElement.Clone());

        await contributor.PreflightAsync(record, CancellationToken.None);
    }

    private static StateRecord CreateDirectionRecord(string stages, string views)
    {
        var json = "{\"stages\":" + stages +
            ",\"views\":" + views +
            ",\"layout\":\"single\",\"bindings\":{\"main\":\"main\"}," +
            "\"effects\":{\"curtain\":{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":1},\"opacity\":0}}," +
            "\"revision\":0}";
        return new StateRecord(
            BuiltInStateKeys.Direction,
            StateScope.Game,
            1,
            JsonDocument.Parse(json).RootElement.Clone());
    }

    [Fact]
    public void PlannerBuildsCompleteTargetStateAndResolvesSameBeatDefinitions()
    {
        var planner = new DirectionPlanner();
        var cell = new StageId("cell");
        var reaction = new ViewId("reaction");
        var ema = new ActorId("Ema");

        var plan = planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(reaction, source: new StageViewSource(cell), zoom: 1.8f),
                Actor(ema, cell, position: new DirectionPoint(70, 0)),
                Actor(ema, cell, appearance: "Fearful1,ArmR1"),
                new BackdropPatch(
                    cell,
                    new BackdropState(new ResourceKey("Background_025_001")),
                    DirectionTransitionSpec.Cut),
                new ScreenPatch(
                    new LayoutId("split"),
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [new LayoutSlotId("main")] = ViewId.Main,
                        [new LayoutSlotId("inset")] = reaction,
                    },
                    DirectionTransitionSpec.Cut),
            });

        var stage = plan.TargetState.Stages[cell];
        Assert.Equal("Background_025_001", stage.Backdrop?.Resource.ToString());
        Assert.Equal(new DirectionPoint(70, 0), stage.Actors[ema].Position);
        Assert.Equal("Fearful1,ArmR1", stage.Actors[ema].Appearance);
        Assert.Equal(cell, Assert.IsType<StageViewSource>(
            plan.TargetState.Views[reaction].Source).StageId);
        Assert.Equal(1.8f, plan.TargetState.Views[reaction].Pose.Zoom);
        Assert.Equal(reaction, plan.TargetState.Screen.Bindings[new LayoutSlotId("inset")]);
        Assert.Equal(1, plan.TargetState.Revision);
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.Backdrop &&
            claim.SemanticId == "Background_025_001");
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.ActorAppearance &&
            claim.SemanticId == "Ema/Fearful1,ArmR1");
    }

    [Fact]
    public void PlannerRejectsTwoWritesToTheSameStablePathWithLocations()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Trial", 12, 1);
        var second = new DirectionSourceLocation("Trial", 13, 2);

        var exception = Assert.Throws<DirectionConflictException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f, location: first),
                View(ViewId.Main, zoom: 2, location: second),
            }));

        Assert.Equal("view/main/zoom", exception.StablePath);
        Assert.Equal(first, exception.First);
        Assert.Equal(second, exception.Second);
        Assert.Contains("Trial:12#1", exception.Message);
        Assert.Contains("Trial:13#2", exception.Message);
    }

    [Fact]
    public void PlannerRejectsUnknownStageAndViewReferences()
    {
        var planner = new DirectionPlanner();

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new[]
            {
                View(ViewId.Main, source: new StageViewSource(new StageId("missing"))),
            }));

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new ScreenPatch(
                    LayoutId.Single,
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [LayoutSlotId.Main] = new ViewId("missing"),
                    },
                    DirectionTransitionSpec.Cut),
            }));
    }

    [Fact]
    public void CgChangesViewSourceWithoutMutatingStage()
    {
        var before = DirectionState.CreateDefault();
        var planner = new DirectionPlanner();

        var plan = planner.Plan(before, new[]
        {
            View(
                ViewId.Main,
                source: new CgViewSource(new ResourceKey("Still_490_001")),
                zoom: 3),
        });

        Assert.Same(before.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main]);
        var source = Assert.IsType<CgViewSource>(plan.TargetState.Views[ViewId.Main].Source);
        Assert.Equal("Still_490_001", source.Resource.ToString());
        Assert.Equal(3, plan.TargetState.Views[ViewId.Main].Pose.Zoom);
    }

    [Fact(Timeout = 5_000)]
    public async Task PrepareFailureDoesNotPublishStateOrCommitBackend()
    {
        var backend = new FakeDirectionBackend { PrepareException = new ExpectedException() };
        var coordinator = CreateCoordinator(backend);
        var before = coordinator.State;

        await Assert.ThrowsAsync<ExpectedException>(() => coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }));

        Assert.Same(before, coordinator.State);
        Assert.Equal(0, backend.CommitCount);
        Assert.Null(backend.VisibleState);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommitPublishesOneRevisionAndUsesPlanToken()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);

        var transaction = await coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        Assert.Equal(1, coordinator.State.Revision);
        Assert.Equal(coordinator.State, backend.VisibleState);
        Assert.Equal(transaction.Plan.Id, backend.LastToken?.TransactionId);
        Assert.Equal(1, backend.LastToken?.Revision);
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.Equal(1, backend.CommitCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommitBarrierReturnsBeforeSettlementAndSkipFinishesTransition()
    {
        var backend = new FakeDirectionBackend();
        var coordinator = CreateCoordinator(backend);

        var transaction = await coordinator.ExecuteAsync(new[]
        {
            View(
                ViewId.Main,
                zoom: 2,
                transition: new DirectionTransitionSpec(
                    DirectionTransitionKind.Move,
                    2,
                    Skippable: true)),
        });

        Assert.Equal(DirectionTransactionStatus.Committed, transaction.Status);
        Assert.False(transaction.Settlement.IsCompleted);

        await coordinator.FinishSkippableAsync();

        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.Equal(1, backend.FinishCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task RollbackRestoresBeforeStateAsANewRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var transaction = await coordinator.ExecuteAsync(new[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        var rollback = await coordinator.RollbackAsync(transaction);

        Assert.Null(coordinator.State.Stages[StageId.Main].Backdrop);
        Assert.Equal(2, coordinator.State.Revision);
        Assert.Equal(1, rollback.Plan.BeforeState.Revision);
        Assert.Equal(2, backend.CommitCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task RollbackRejectsStaleTransactionRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var first = await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, zoom: 2),
        }, DirectionCompletionBarrier.Settle);
        await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, rotation: 10),
        }, DirectionCompletionBarrier.Settle);

        var exception = await Assert.ThrowsAsync<DirectionRevisionException>(() =>
            coordinator.RollbackAsync(first));

        Assert.Equal(1, exception.Expected);
        Assert.Equal(2, exception.Actual);
    }

    [Fact(Timeout = 5_000)]
    public async Task BeatCollectsExplicitlyAndCommitsOneRevision()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("opening", "Adv02");

        var first = await beats.SubmitAsync(new BackdropPatch(
            StageId.Main,
            new BackdropState(new ResourceKey("Background_001_001")),
            DirectionTransitionSpec.Cut));
        var second = await beats.SubmitAsync(View(ViewId.Main, zoom: 2));

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(0, coordinator.State.Revision);
        Assert.Equal(0, backend.CommitCount);

        await beats.EndAsync();

        Assert.Equal(1, coordinator.State.Revision);
        Assert.Equal(1, backend.CommitCount);
        Assert.Equal(2, coordinator.State.Views[ViewId.Main].Pose.Zoom);
    }

    [Fact(Timeout = 5_000)]
    public async Task TransientCueIsExecutedWithTheCommittedTransactionToken()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var cueExecutor = new RecordingCueExecutor();
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator, cueExecutor);
        beats.Begin("cue-beat", "Opening");
        await beats.SubmitAsync(new DirectionCue(
            "test", new Dictionary<string, string>()));

        var transaction = await beats.EndAsync(DirectionCompletionBarrier.Commit);

        Assert.Equal(transaction.Token, cueExecutor.Token);
        Assert.Single(cueExecutor.Cues);
        Assert.Equal(transaction.Plan.Id, cueExecutor.Token?.TransactionId);
        Assert.Equal(1, cueExecutor.Token?.Revision);
    }

    [Fact]
    public void PlannerReportsBothLocationsForConflictingComponentTransitions()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Opening", 4, 0);
        var second = new DirectionSourceLocation("Opening", 5, 0);

        var exception = Assert.Throws<DirectionBeatException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f,
                    transition: new DirectionTransitionSpec(DirectionTransitionKind.Move, 1),
                    location: first),
                View(ViewId.Main, rotation: 10,
                    transition: new DirectionTransitionSpec(DirectionTransitionKind.Fade, 1),
                    location: second),
            }));

        Assert.Contains("Opening:4#0", exception.Message);
        Assert.Contains("Opening:5#0", exception.Message);
    }

    [Fact]
    public void PlannerPreservesCutPoseAsStartOfFollowingMove()
    {
        var cg = new CgViewSource(new ResourceKey("Still_490_001"));
        var cutPose = new ViewPose(new DirectionPoint(37.384f, 74.816f), 3, 10);
        var movePose = new ViewPose(new DirectionPoint(44.438f, 71.950f), 3, 10);
        var plan = new DirectionPlanner().Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new ViewPatch(
                    ViewId.Main,
                    DirectionValue<ViewSource>.From(cg),
                    default,
                    default,
                    default,
                    DirectionTransitionSpec.Cut),
                new ViewPatch(
                    ViewId.Main,
                    default,
                    DirectionValue<DirectionPoint>.From(cutPose.Center),
                    DirectionValue<float>.From(cutPose.Zoom),
                    DirectionValue<float>.From(cutPose.RotationDegrees),
                    DirectionTransitionSpec.Cut),
                new ViewPatch(
                    ViewId.Main,
                    default,
                    DirectionValue<DirectionPoint>.From(movePose.Center),
                    default,
                    default,
                    new DirectionTransitionSpec(
                        DirectionTransitionKind.Move,
                        0.4,
                        DirectionEase.SineOut)),
            });

        var sequence = Assert.Single(plan.ViewSequences!);
        Assert.Equal(cutPose, sequence.StartState.Pose);
        Assert.Equal(DirectionTransitionKind.Move, sequence.Transition.Kind);
        Assert.Equal(movePose, plan.TargetState.Views[ViewId.Main].Pose);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdjacentDirectivesOutsideBeatRemainSeparateTransactions()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);

        await beats.SubmitAsync(new BackdropPatch(
            StageId.Main,
            new BackdropState(new ResourceKey("Background_001_001")),
            DirectionTransitionSpec.Cut), DirectionCompletionBarrier.Settle);
        await beats.SubmitAsync(
            View(ViewId.Main, zoom: 2),
            DirectionCompletionBarrier.Settle);

        Assert.Equal(2, coordinator.State.Revision);
        Assert.Equal(2, backend.CommitCount);
    }

    [Fact]
    public async Task OpenBeatCanAbortButCannotNestOrCrossNodeBoundary()
    {
        var coordinator = CreateCoordinator(new FakeDirectionBackend { AutoSettle = true });
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("opening", "Adv02");
        await beats.SubmitAsync(View(ViewId.Main, zoom: 2));

        Assert.Throws<DirectionBeatException>(() => beats.Begin("nested", "Adv02"));
        Assert.Throws<DirectionBeatException>(() => beats.ValidateNodeBoundary("Adv02"));

        beats.AbortOpen();

        Assert.False(beats.HasOpenBeat);
        Assert.Equal(0, coordinator.State.Revision);
    }

    [Fact(Timeout = 5_000)]
    public async Task NamedBeatCanJoinAndFinishAfterCommitBarrier()
    {
        var backend = new FakeDirectionBackend();
        var coordinator = CreateCoordinator(backend);
        var beats = new DirectionBeatRuntime(coordinator);
        beats.Begin("slow-pan", "Trial");
        await beats.SubmitAsync(View(
            ViewId.Main,
            zoom: 2,
            transition: new DirectionTransitionSpec(DirectionTransitionKind.Move, 4)));
        await beats.EndAsync(DirectionCompletionBarrier.Commit);

        var join = beats.JoinAsync("slow-pan");
        Assert.False(join.IsCompleted);

        await beats.FinishAsync("slow-pan");
        await join;

        Assert.Equal(1, backend.FinishCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeBackendPreparesEveryChangedComponentBeforeCommit()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components,
            components,
            components,
            components);
        var planner = new DirectionPlanner();
        var plan = planner.Plan(DirectionState.CreateDefault(), new IDirectionDirective[]
        {
            new BackdropPatch(
                StageId.Main,
                new BackdropState(new ResourceKey("Background_001_001")),
                DirectionTransitionSpec.Cut),
            View(ViewId.Main, zoom: 2),
        });

        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);

        Assert.Equal(new[] { "stage:main", "view:main" }, components.PrepareCalls);
        Assert.Empty(components.CommitTokens);

        var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
        prepared.Commit(token);

        Assert.Equal(2, components.CommitTokens.Count);
        Assert.All(components.CommitTokens, actual => Assert.Equal(token, actual));
        Assert.False(prepared.Settlement.IsCompleted);

        await prepared.FinishAsync(CancellationToken.None);
        await prepared.Settlement;
        await prepared.DisposeAsync();

        Assert.Equal(2, components.FinishCount);
        Assert.Equal(2, components.DisposeCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeBackendDisposesSuccessfulPreparationsWhenAnotherFails()
    {
        var components = new ComponentBackendHarness { FailViewPreparation = true };
        var backend = new CompositeDirectionPreparationBackend(
            components,
            components,
            components,
            components);
        var plan = new DirectionPlanner().Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(new ResourceKey("Background_001_001")),
                    DirectionTransitionSpec.Cut),
                View(ViewId.Main, zoom: 2),
            });

        await Assert.ThrowsAsync<ExpectedException>(() =>
            backend.PrepareAsync(plan, CancellationToken.None));

        Assert.Equal(1, components.DisposeCount);
        Assert.Empty(components.CommitTokens);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeCommitFailureCompensatesAlreadyCommittedComponents()
    {
        var components = new ComponentBackendHarness { FailViewCommit = true };
        var backend = new CompositeDirectionPreparationBackend(
            components,
            components,
            components,
            components);
        var plan = new DirectionPlanner().Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(new ResourceKey("Background_001_001")),
                    DirectionTransitionSpec.Cut),
                View(ViewId.Main, zoom: 2),
            });

        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);

        Assert.Throws<ExpectedException>(() => prepared.Commit(
            new DirectionCommitToken(plan.Id, plan.TargetState.Revision)));
        // Rollback is attempted for the component whose Commit threw as well:
        // a backend may have published partial state before reporting failure.
        Assert.Equal(2, components.RollbackCount);
        await prepared.DisposeAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeRollbackIsTerminalAndDisposesEveryComponent()
    {
        var components = new ComponentBackendHarness { ThrowOnDispose = true };
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var plan = new DirectionPlanner().Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new BackdropPatch(
                    StageId.Main,
                    new BackdropState(new ResourceKey("Background_001_001")),
                    DirectionTransitionSpec.Cut),
                View(ViewId.Main, zoom: 2),
            });
        var prepared = await backend.PrepareAsync(plan, CancellationToken.None);
        var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);

        prepared.Commit(token);
        prepared.Rollback(token);
        Assert.Throws<InvalidOperationException>(() => prepared.Commit(token));
        await Assert.ThrowsAsync<AggregateException>(() => prepared.DisposeAsync().AsTask());
        Assert.Equal(2, components.DisposeCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task FaultedSettlementIsNotSilentlyAcceptedByFinish()
    {
        var backend = new FakeDirectionBackend { SettlementException = new ExpectedException() };
        var coordinator = CreateCoordinator(backend);
        var transaction = await coordinator.ExecuteAsync(new[]
        {
            View(ViewId.Main, zoom: 2),
        });

        await Assert.ThrowsAsync<ExpectedException>(() => transaction.Settlement);
        await Assert.ThrowsAsync<ExpectedException>(() => coordinator.FinishAsync(transaction));
    }

    private static DirectionTransactionCoordinator CreateCoordinator(
        IDirectionPreparationBackend backend) =>
        new(new DirectionPlanner(), backend);

    private static ActorPatch Actor(
        ActorId actorId,
        StageId stageId,
        DirectionPoint? position = null,
        string? appearance = null) => new(
            stageId,
            actorId,
            position is { } point
                ? DirectionValue<DirectionPoint>.From(point)
                : default,
            appearance is not null
                ? DirectionValue<string?>.From(appearance)
                : default,
            default,
            default,
            DirectionTransitionSpec.Cut);

    private static ViewPatch View(
        ViewId viewId,
        ViewSource? source = null,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            source is not null
                ? DirectionValue<ViewSource>.From(source)
                : default,
            default,
            zoom is { } zoomValue
                ? DirectionValue<float>.From(zoomValue)
                : default,
            rotation is { } rotationValue
                ? DirectionValue<float>.From(rotationValue)
                : default,
            transition ?? DirectionTransitionSpec.Cut,
            location);

    private sealed class FakeDirectionBackend : IDirectionPreparationBackend
    {
        public Exception? PrepareException { get; init; }
        public Exception? SettlementException { get; init; }
        public bool AutoSettle { get; init; }
        public int CommitCount { get; private set; }
        public int FinishCount { get; private set; }
        public DirectionState? VisibleState { get; private set; }
        public DirectionCommitToken? LastToken { get; private set; }

        public Task<IPreparedDirectionTransaction> PrepareAsync(
            DirectionPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareException is not null) throw PrepareException;
            return Task.FromResult<IPreparedDirectionTransaction>(new Prepared(this, plan));
        }

        private sealed class Prepared : IPreparedDirectionTransaction
        {
            private readonly FakeDirectionBackend owner;
            private readonly DirectionPlan plan;
            private readonly TaskCompletionSource settlement = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private bool committed;

            public Prepared(FakeDirectionBackend owner, DirectionPlan plan)
            {
                this.owner = owner;
                this.plan = plan;
            }

            public Task Settlement => settlement.Task;

            public void Commit(DirectionCommitToken token)
            {
                if (committed) throw new InvalidOperationException("Already committed.");
                committed = true;
                owner.CommitCount++;
                owner.LastToken = token;
                owner.VisibleState = plan.TargetState;
                if (owner.SettlementException is not null)
                    settlement.TrySetException(owner.SettlementException);
                else if (owner.AutoSettle) settlement.TrySetResult();
            }

            public ValueTask FinishAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!committed) throw new InvalidOperationException("Not committed.");
                owner.FinishCount++;
                settlement.TrySetResult();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!committed) settlement.TrySetCanceled();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ComponentBackendHarness :
        IDirectionStageBackend,
        IDirectionViewBackend,
        IDirectionScreenBackend,
        IDirectionEffectBackend
    {
        public bool FailViewPreparation { get; init; }
        public bool FailViewCommit { get; init; }
        public bool ThrowOnDispose { get; init; }
        public List<string> PrepareCalls { get; } = new();
        public List<DirectionCommitToken> CommitTokens { get; } = new();
        public int FinishCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int RollbackCount { get; private set; }

        public Task<IPreparedDirectionComponent> PrepareAsync(
            DirectionPlan plan,
            StageId stageId,
            StageState? before,
            StageState? target,
            CancellationToken cancellationToken) =>
            Prepare($"stage:{stageId}", fail: false, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            DirectionPlan plan,
            ViewId viewId,
            ViewState? before,
            ViewState? target,
            CancellationToken cancellationToken) =>
            Prepare($"view:{viewId}", FailViewPreparation, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            DirectionPlan plan,
            ScreenState before,
            ScreenState target,
            CancellationToken cancellationToken) =>
            Prepare("screen", fail: false, cancellationToken);

        public Task<IPreparedDirectionComponent> PrepareAsync(
            DirectionPlan plan,
            StableEffectState before,
            StableEffectState target,
            CancellationToken cancellationToken) =>
            Prepare("effects", fail: false, cancellationToken);

        private Task<IPreparedDirectionComponent> Prepare(
            string name,
            bool fail,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls.Add(name);
            if (fail) return Task.FromException<IPreparedDirectionComponent>(new ExpectedException());
            return Task.FromResult<IPreparedDirectionComponent>(new Component(this, name));
        }

        private sealed class Component : IPreparedDirectionComponent
        {
            private readonly ComponentBackendHarness owner;
            private readonly string name;
            private readonly TaskCompletionSource settlement = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Component(ComponentBackendHarness owner, string name)
            {
                this.owner = owner;
                this.name = name;
            }

            public Task Settlement => settlement.Task;

            public void Commit(DirectionCommitToken token)
            {
                if (owner.FailViewCommit && name == "view:main")
                    throw new ExpectedException();
                owner.CommitTokens.Add(token);
            }

            public void Rollback(DirectionCommitToken token) => owner.RollbackCount++;

            public ValueTask FinishAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.FinishCount++;
                settlement.TrySetResult();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                if (owner.ThrowOnDispose)
                    throw new ExpectedException();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ExpectedException : Exception;

    private sealed class VersionStoreResourceManager : IResourceManager
    {
        public int LoadedResourceCount => 0;

        public TResource Load<TResource>(ResourceKey key, string? overridePath = null)
            where TResource : class => throw new NotSupportedException();

        public Task<TResource> LoadAsync<TResource>(
            ResourceKey key,
            string? overridePath = null,
            CancellationToken cancellationToken = default)
            where TResource : class => throw new NotSupportedException();

        public bool Exists<TResource>(ResourceKey key, string? overridePath = null)
            where TResource : class => false;

        public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
            where TResource : class => new VersionStoreResourceLoader<TResource>(pathPrefix);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class VersionStoreResourceLoader<TResource> : IResourceLoader<TResource>
        where TResource : class
    {
        public VersionStoreResourceLoader(string pathPrefix) => PathPrefix = pathPrefix;
        public string PathPrefix { get; }
        public ResourceKey GetKey(string localPath) => new($"{PathPrefix}/{localPath}");
        public bool Exists(string localPath, string? overridePath = null) => false;
        public TResource Load(string localPath, string? overridePath = null) => throw new NotSupportedException();
        public Task<TResource> LoadAsync(
            string localPath,
            string? overridePath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TResource>(new NotSupportedException());
    }

    private sealed class RecordingCueExecutor : ITransientDirectionCueExecutor
    {
        public List<ITransientDirectionCue> Cues { get; } = new();
        public DirectionCommitToken? Token { get; private set; }

        public Task ExecuteAsync(
            IEnumerable<ITransientDirectionCue> cues,
            DirectionCompletionBarrier completion,
            CancellationToken cancellationToken)
        {
            Cues.AddRange(cues);
            return Task.CompletedTask;
        }

        public Task ExecuteCommittedAsync(
            IEnumerable<ITransientDirectionCue> cues,
            DirectionCommitToken token,
            DirectionCompletionBarrier completion,
            CancellationToken cancellationToken)
        {
            Token = token;
            return ExecuteAsync(cues, completion, cancellationToken);
        }
    }
}

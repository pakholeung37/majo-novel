#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.UnitTests.Testing;
using YarnSpinnerGodot;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class NodeDirectionCompilerTests
{
    [Fact]
    public void NodeDirectionCompilerBuildsNodeLocalSymbolsAndTypedPreloadReferences()
    {
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
                    "title: Opening\n---\n<<stage define cell>>\n<<view define reaction stage:cell>>\n<<backdrop Background_001>>\n<<camera pose view:reaction zoom:2>>\n<<backdrop Still_001 stage:cell>>\n<<layout split main:main inset:reaction>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        Assert.Equal("Opening", module.NodeId);
        Assert.Contains("cell", module.Symbols.Stages);
        Assert.Contains("reaction", module.Symbols.Views);
        Assert.Contains(module.Resources, resource =>
            resource.Kind == DirectionResourceKind.Backdrop &&
            resource.SemanticId == "Background_001");
        Assert.Contains(module.Resources, resource =>
            resource.Kind == DirectionResourceKind.Backdrop &&
            resource.SemanticId == "Still_001");
        Assert.Equal(6, module.SourceMaps.Count);
        Assert.Equal(2, module.Resources.Count);
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
                    "title: Opening\n---\n<<mark focus at:25,75>>\n<<character Ema at:focus>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot, 42));

        Assert.Equal(42, module.Version);
        var mark = Assert.Single(module.MarkDefinitions);
        Assert.Equal("focus", mark.Name);
        Assert.Equal(new DirectionPoint(25, 75), mark.Position);
        Assert.Contains("focus", module.Symbols.Marks);
    }

    [Fact]
    public void NodeDirectionCompilerCollectsShotAliasesWithoutCompileDiagnostics()
    {
        var snapshot = new YarnSourceSnapshot(
            "test", "test.yarnproject", 1, "en",
            new[]
            {
                new YarnSourceFileSnapshot(
                    "main.yarn", "main.yarn",
                    "title: Opening\n---\n<<camera pose zoom:2 as:close>>\n<<shot use close>>\n<<shot use missing>>\n===\n",
                    "hash"),
            },
            "fingerprint");

        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));

        Assert.Contains("close", module.Symbols.ShotAliases);
        Assert.Empty(module.Diagnostics);
    }

    [Fact]
    public void NodeDirectionCompilerCollectsExplicitImportsWithoutDiagnostics()
    {
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

        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        var import = Assert.Single(module.Imports);
        Assert.Equal("stage", import.Kind);
        Assert.Equal("news", import.Alias);
        Assert.Equal("Broadcast", import.SourceNode);
        Assert.Empty(module.Diagnostics);
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
                    DirectionResourceKind.Backdrop, "Still_001", "main.yarn", 4, 1),
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
        var patch = new ViewPosePatch(
            ViewId.Main,
            default,
            DirectionValue.From(1.5f),
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
}

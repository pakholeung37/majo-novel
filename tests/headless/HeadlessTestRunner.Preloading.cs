#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Majo.Runtime.Actors;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn.Preloading;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateWindowPreloadingAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests/headless/fixtures/resources";
        configuration.Resources.PreloadTextureEstimateMiB = 1;
        configuration.Presentation!.Enabled = false;
        configuration.Actors!.ReferenceResolution = new Vector2I(2560, 1440);
        var bootstrap = new MajoRuntimeBootstrap { Name = "WindowPreloadRuntime", Configuration = configuration };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var services = bootstrap.Host!.Services;
        var resources = services.Get<IResourceManager>();
        var actors = services.Get<IActorManager>();
        var program = new global::Yarn.Program();
        var node = new global::Yarn.Node { Name = "Start" };
        node.Instructions.Add(new global::Yarn.Instruction
        {
            RunCommand = new global::Yarn.Instruction.Types.RunCommandInstruction { CommandText = "backdrop room" },
        });
        node.Instructions.Add(new global::Yarn.Instruction
        {
            RunCommand = new global::Yarn.Instruction.Types.RunCommandInstruction { CommandText = "character Plain appearance:1" },
        });
        node.Instructions.Add(new global::Yarn.Instruction
        {
            RunLine = new global::Yarn.Instruction.Types.RunLineInstruction { LineID = "line:preload-voice" },
        });
        program.Nodes.Add("Start", node);
        using var project = new YarnProject { CompiledYarnProgramBase64 = Convert.ToBase64String(program.ToByteArray()) };
        using var preloader = new YarnWindowPreloader(resources, configuration.Resources, configuration.Audio!,
            services.Get<ILocaleManager>(), services.Get<ILocalizedResourceResolver>(), actors);
        await preloader.PrepareStartAsync(project, "Start", CancellationToken.None, "line:preload-voice");
        preloader.EnterNode(project, "Start");
        var restoredWindow = resources.CaptureCacheSnapshot();
        Assert(restoredWindow.Entries.Any(entry => entry.ResolvedPath.Contains("preload-voice", StringComparison.Ordinal)) &&
            !restoredWindow.Entries.Any(entry => entry.ResolvedPath.Contains("Plain/1", StringComparison.Ordinal)),
            "Restore window loaded the node opening instead of the saved voice line.");
        await preloader.PrepareStartAsync(project, "Start", CancellationToken.None);
        var snapshot = resources.CaptureCacheSnapshot();
        Assert(snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("room", StringComparison.Ordinal)) &&
            snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("Plain/1", StringComparison.Ordinal)) &&
            snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("preload-voice", StringComparison.Ordinal)),
            "Opening window did not preload background, character and voice before execution.");
        Assert(actors.Actors.Count == 0, "Resource discovery created actors prematurely.");

        var misses = snapshot.CacheMisses;
        await preloader.BeforeCommandAsync("backdrop", ["room"], CancellationToken.None);
        await resources.LoadAsync<Texture2D>(new ResourceKey("backgrounds/room"));
        await preloader.BeforeCommandAsync("character", ["Plain", "appearance:1"], CancellationToken.None);
        await resources.LoadAsync<Texture2D>(new ResourceKey("characters/Plain/1"));
        await preloader.BeforeLineAsync(new DialogueLine("line:preload-voice", "LayeredA",
            new DialogueText(string.Empty, Array.Empty<DialogueMarkupSpan>()), Array.Empty<string>()), CancellationToken.None);
        await resources.LoadAsync<AudioStream>(new ResourceKey("audio/voice/preload-voice"));
        Assert(resources.CaptureCacheSnapshot().CacheMisses == misses,
            "Resource use missed the cache after the preload barrier.");
        var layered = actors.GetPreloadResources(new ActorSpawnRequest("LayeredA", ActorFactoryIds.Character, "Surprised1"));
        Assert(layered.Count > 0 && layered.All(entry => entry.OverridePath?.Contains("atlas", StringComparison.Ordinal) == true)
            && actors.Actors.Count == 0, "Layered preload discovery did not return atlases without spawning actors.");
        var coordinator = services.Get<IDirectionTransactionCoordinator>();
        await preloader.BeforeCommandAsync("character", ["LayeredA", "appearance:Surprised1"], CancellationToken.None);
        await coordinator.ExecuteAsync(
            [ActorPatchFor("LayeredA", new DirectionPoint(50, 0), "Surprised1", 1, 0)], DirectionCompletionBarrier.Settle);
        var atlas = layered[0];
        Assert(resources.IsPinned<Texture2D>(atlas.Key, atlas.OverridePath), "Active Direction tree did not retain its atlas.");
        var treeMisses = resources.CaptureCacheSnapshot().CacheMisses;
        for (var index = 0; index < 3; index++)
        {
            await preloader.BeforeCommandAsync("character", ["LayeredA", "appearance:Surprised1"], CancellationToken.None);
            resources.TrimCache();
            await coordinator.ExecuteAsync(
                [ActorPatchFor("LayeredA", new DirectionPoint(50 + index, 0), "Surprised1", 1, 0)], DirectionCompletionBarrier.Settle);
            Assert(resources.CaptureCacheSnapshot().CacheMisses == treeMisses,
                "Replacement Direction tree reloaded an atlas retained by the previous tree.");
        }
        await preloader.BeforeCommandAsync("character", ["LayeredB", "appearance:Surprised1"], CancellationToken.None);
        await coordinator.ExecuteAsync(
            [ActorPatchFor("LayeredB", new DirectionPoint(30, 0), "Surprised1", 1, 0),
             ActorPatchFor("LayeredA", new DirectionPoint(70, 0), "Fearful1", 1, 0)], DirectionCompletionBarrier.Settle);
        var pairSnapshot = resources.CaptureCacheSnapshot();
        Assert(pairSnapshot.Entries.Single(entry => entry.ResolvedPath.Contains("LayeredA/atlas", StringComparison.Ordinal)).EstimatedBytes == 1024L * 1024 * 4,
            "LayeredA estimate charged mipmaps disabled by the texture importer.");
        Assert(pairSnapshot.Entries.Single(entry => entry.ResolvedPath.Contains("LayeredB/atlas", StringComparison.Ordinal)).EstimatedBytes == 512L * 512 * 4,
            "LayeredB estimate charged mipmaps disabled by the texture importer.");
        Assert(!pairSnapshot.IsOverBudget, "LayeredB/LayeredA pair exceeded the global cache budget.");
        preloader.Clear();
        Assert(resources.IsPinned<Texture2D>(atlas.Key, atlas.OverridePath), "Preload window release invalidated an active tree.");
        var savedDirection = coordinator.State;
        for (var index = 0; index < 3; index++)
        {
            await coordinator.ResetAsync(CancellationToken.None);
            await WaitForConditionAsync(() => resources.CaptureCacheSnapshot().ActiveLeaseCount == 0, 2);
            Assert(!resources.IsPinned<Texture2D>(atlas.Key, atlas.OverridePath),
                "Restore reset left an old actor tree pinned.");
            await coordinator.RestoreAsync(savedDirection, CancellationToken.None);
            Assert(resources.IsPinned<Texture2D>(atlas.Key, atlas.OverridePath),
                "Restored actor tree did not retain its atlas.");
        }
        await coordinator.ResetAsync(CancellationToken.None);
        await WaitForConditionAsync(() => resources.CaptureCacheSnapshot().ActiveLeaseCount == 0, 2);
        await bootstrap.RequestShutdownAsync();
    }
}

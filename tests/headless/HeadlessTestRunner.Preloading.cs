#nullable enable

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Majo.Demos.Manosaba.Actors.Layered;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
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
        configuration.Actors.Actors.Add(new ActorDefinitionConfiguration
        {
            Id = "UnusedLayered",
            FactoryId = LayeredActorIds.Factory,
            FactoryConfiguration = new LayeredCharacterConfiguration
            {
                AssetId = "UnusedLayered",
                ManifestPath = "res://missing-unused-layered.json",
            },
        });
        configuration.Actors!.Actors.Add(CreateActorDefinition(
            "room", ActorFactoryIds.Background, CharacterPositionPreset.Center,
            ("Default", "res://tests/headless/fixtures/resources/backgrounds/room.png")));

        configuration.Actors.Actors.Add(CreateActorDefinition("Composite", "test.background-metadata",
            CharacterPositionPreset.Center, ("Day", "res://tests/headless/fixtures/resources/backgrounds/room.png")));
        var preloadedBackgrounds = new List<string>();
        var manifestReads = new Dictionary<string, int>(StringComparer.Ordinal);
        var cache = new LayeredCharacterManifestCache(path =>
        {
            manifestReads[path] = manifestReads.GetValueOrDefault(path) + 1;
            return Task.FromResult(LayeredCharacterManifest.Parse(FileAccess.GetFileAsString(path)));
        });
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "WindowPreloadRuntime",
            Configuration = configuration,
            ConfigureServices = registry => registry.ReplaceSingleton<LayeredCharacterManifestCache>(_ => cache),
            ConfigureActorFactories = registry => registry.Register("test.background-metadata", ActorFactoryDomains.Direction,
                context =>
                {
                    Assert(GodotObject.IsInstanceValid(context.BackgroundRoot), "Direction metadata factory requires a valid root.");
                    return new PreloadBackgroundMetadataFactory(preloadedBackgrounds);
                }, ActorConfigurationCompiler.CompileBackgroundTextureDefinition),
        };
        bootstrap.AddPlugin(new LayeredCharacterPlugin());
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var services = bootstrap.Host!.Services;
        Assert(manifestReads.Count == 0, "Runtime initialization loaded unrequested layered metadata.");
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
            services.Get<ILocaleManager>(), services.Get<ILocalizedResourceResolver>(), actors,
            directionDefinitions: services.Get<DirectionActorDefinitionEnvironment>().Resolver);
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
        await resources.LoadAsync<Texture2D>(new ResourceKey("backgrounds/room/Default"),
            "res://tests/headless/fixtures/resources/backgrounds/room.png");
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

        var left = ActorPatchFor("ema_left", new DirectionPoint(35, 0), "Normal1,ArmR3", 1, 0) with
        { DefinitionId = "LayeredA" };
        var leftIncrement = ActorPatch.Place(StageId.Main, new ActorId("ema_left")) with
        {
            DefinitionId = "LayeredA",
            Appearance = DirectionValue.From<string?>("Fearful1"),
        };
        var right = ActorPatchFor("ema_right", new DirectionPoint(65, 0), "Surprised1", 1, 0) with
        { DefinitionId = "LayeredA" };
        await coordinator.ExecuteAsync([left, leftIncrement, right], DirectionCompletionBarrier.Settle);
        var leftState = coordinator.State.Stages[StageId.Main].Actors[new ActorId("ema_left")];
        Assert(manifestReads.Values.All(count => count == 1) && !manifestReads.ContainsKey("res://missing-unused-layered.json"),
            "Actor factories did not share metadata loads or validated an unrelated definition.");
        Assert(leftState.DefinitionId == "LayeredA" && leftState.Appearance?.StartsWith("-,", StringComparison.Ordinal) == true,
            "Same-beat incremental appearances did not commit a complete canonical value.");
        Assert(leftState.Appearance!.Contains("Arm+Raised", StringComparison.Ordinal) &&
            leftState.Appearance.Contains("Eyes+Fearful", StringComparison.Ordinal),
            "Same-beat appearance resolution lost the first patch's arm or the second patch's eyes.");
        Assert(coordinator.State.Stages[StageId.Main].Actors[new ActorId("ema_right")].DefinitionId == "LayeredA",
            "Two instances did not retain their shared resource definition identity.");
        await coordinator.ExecuteAsync([new ActorHidePatch(StageId.Main, new ActorId("ema_left"),
            DirectionActorKind.Character, DirectionTransitionSpec.Cut)], DirectionCompletionBarrier.Settle);
        Assert(coordinator.State.Stages[StageId.Main].Actors[new ActorId("ema_right")].Visible &&
            resources.IsPinned<Texture2D>(atlas.Key, atlas.OverridePath),
            "Destroying one aliased instance invalidated the other instance's shared atlas.");

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
        using (var metadataPreloader = new YarnWindowPreloader(resources, configuration.Resources, configuration.Audio!,
            services.Get<ILocaleManager>(), services.Get<ILocalizedResourceResolver>(), actors,
            directionDefinitions: services.Get<DirectionActorDefinitionEnvironment>().Resolver))
        {
            await metadataPreloader.BeforeCommandAsync("backdrop", ["Composite", "appearance:+Rain"], CancellationToken.None);
            Assert(preloadedBackgrounds.Count == 0, "Preload guessed an unknown incremental appearance baseline.");
            await metadataPreloader.BeforeCommandAsync("beat", ["begin"], CancellationToken.None);
            foreach (var appearance in new[] { "Night", "+Rain", "Night", "+Snow" })
                await metadataPreloader.BeforeCommandAsync("backdrop", ["Composite", "appearance:" + appearance], CancellationToken.None);
            Assert(preloadedBackgrounds.Contains("Night+Rain") && preloadedBackgrounds.Contains("Night+Snow") &&
                !preloadedBackgrounds.Contains("Night+Rain+Snow"), "Preload lost a repeated backdrop baseline reset.");
            Assert(resources.CaptureCacheSnapshot().Entries.Any(entry => entry.ResolvedPath.Contains("room", StringComparison.Ordinal)) &&
                resources.CaptureCacheSnapshot().Entries.Any(entry => entry.ResolvedPath.Contains("Plain/1", StringComparison.Ordinal)),
                "Direction-only backdrop factory did not preload its multiple textures.");
            Assert(!actors.TryGet("Composite", out _), "Backdrop metadata created a Runtime actor.");
        }
        await bootstrap.RequestShutdownAsync();
    }
    private sealed class PreloadBackgroundMetadataFactory(List<string> appearances) : IActorFactory
    {
        public string FactoryId => "test.background-metadata";
        public string GetInitialAppearance(ActorDefinition definition) => "Day";
        public bool IsIncrementalAppearance(string appearance) => appearance.StartsWith('+');
        public string ResolveAppearance(ActorDefinition definition, string sourceAppearance, string appearance) =>
            IsIncrementalAppearance(appearance) ? sourceAppearance + appearance : appearance;
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance)
        {
            appearances.Add(appearance);
            return [
                ResourcePreloadEntry.Create<Texture2D>(new("backgrounds/room/Default"),
                    "res://tests/headless/fixtures/resources/backgrounds/room.png"),
                ResourcePreloadEntry.Create<Texture2D>(new("characters/Plain/1")),
                ResourcePreloadEntry.Create<object>(new("unsupported-background-resource")),
            ];
        }
        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Background metadata must never create an actor.");
    }

}

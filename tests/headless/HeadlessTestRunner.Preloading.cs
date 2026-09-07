#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn.Preloading;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateWindowPreloadingAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://demos/manosaba/assets";
        configuration.Presentation!.Enabled = false;
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
            RunCommand = new global::Yarn.Instruction.Types.RunCommandInstruction { CommandText = "backdrop Background_025_001" },
        });
        node.Instructions.Add(new global::Yarn.Instruction
        {
            RunCommand = new global::Yarn.Instruction.Types.RunCommandInstruction { CommandText = "character Warden appearance:1" },
        });
        node.Instructions.Add(new global::Yarn.Instruction
        {
            RunLine = new global::Yarn.Instruction.Types.RunLineInstruction { LineID = "line:0101Adv02_Ema014" },
        });
        program.Nodes.Add("Start", node);
        using var project = new YarnProject { CompiledYarnProgramBase64 = Convert.ToBase64String(program.ToByteArray()) };
        using var preloader = new YarnWindowPreloader(resources, configuration.Resources, configuration.Audio!,
            services.Get<ILocaleManager>(), services.Get<ILocalizedResourceResolver>(), actors);
        await preloader.PrepareStartAsync(project, "Start", CancellationToken.None);
        var snapshot = resources.CaptureCacheSnapshot();
        Assert(snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("Background_025_001", StringComparison.Ordinal)) &&
            snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("Warden/1", StringComparison.Ordinal)) &&
            snapshot.Entries.Any(entry => entry.ResolvedPath.Contains("0101Adv02_Ema014", StringComparison.Ordinal)),
            "Opening window did not preload background, character and voice before execution.");
        Assert(actors.Actors.Count == 0, "Resource discovery created actors prematurely.");

        var misses = snapshot.CacheMisses;
        await preloader.BeforeCommandAsync("backdrop", ["Background_025_001"], CancellationToken.None);
        await resources.LoadAsync<Texture2D>(new ResourceKey("backgrounds/Background_025_001"));
        await preloader.BeforeCommandAsync("character", ["Warden", "appearance:1"], CancellationToken.None);
        await resources.LoadAsync<Texture2D>(new ResourceKey("characters/Warden/1"));
        await preloader.BeforeLineAsync(new DialogueLine("line:0101Adv02_Ema014", "Ema",
            new DialogueText(string.Empty, Array.Empty<DialogueMarkupSpan>()), Array.Empty<string>()), CancellationToken.None);
        await resources.LoadAsync<AudioStream>(new ResourceKey("audio/voice/0101Adv02_Ema014"));
        Assert(resources.CaptureCacheSnapshot().CacheMisses == misses,
            "Resource use missed the cache after the preload barrier.");
        var layered = actors.GetPreloadResources(new ActorSpawnRequest("Ema", ActorFactoryIds.Character, "Surprised1"));
        Assert(layered.Count > 0 && layered.All(entry => entry.OverridePath?.Contains("Ema_Angle01", StringComparison.Ordinal) == true)
            && actors.Actors.Count == 0, "Layered preload discovery did not return atlases without spawning actors.");
        preloader.Clear();
        await WaitForConditionAsync(() => resources.CaptureCacheSnapshot().ActiveLeaseCount == 0, 2);
        await bootstrap.RequestShutdownAsync();
    }
}

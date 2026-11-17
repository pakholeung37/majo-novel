#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.YarnBridge;
using YarnSpinnerGodot;
using DialogueLine = Majo.Runtime.Dialogue.DialogueLine;
using DialogueOption = Majo.Runtime.Dialogue.DialogueOption;

namespace CoreConsumerSmoke;

public partial class Smoke : Node
{
    public override async void _Ready()
    {
        try
        {
            var packaged = OS.GetCmdlineUserArgs().Contains("--majo-export-smoke");
            if (packaged && !string.IsNullOrEmpty(ProjectSettings.GlobalizePath("res://")))
                throw new InvalidOperationException("Export smoke did not load a packed project.");
            if (packaged && typeof(Smoke).Assembly.GetType("Majo.Runtime.MajoRuntimePlugin") is not null)
                throw new InvalidOperationException("Packed smoke loaded an editor assembly instead of ExportRelease.");
            foreach (var path in new[] {
                "res://addons/majo_runtime/Runtime/UI/Dialogue/DefaultVnPresentation.tscn",
                "res://addons/majo_runtime/Runtime/UI/SaveLoad/DefaultSaveLoadOverlay.tscn",
                "res://addons/majo_runtime/Runtime/Direction/Shaders/PremultipliedOutput.gdshader",
                "res://addons/majo_runtime/Runtime/Direction/Shaders/DissolveTransition.gdshader",
                "res://addons/majo_runtime/Runtime/Direction/Shaders/ScreenSlot.gdshader" })
                if (!ResourceLoader.Exists(path)) throw new InvalidOperationException("Missing packed resource: " + path);
            foreach (var path in new[] {
                "res://addons/majo_runtime/Runtime/UI/Dialogue/DefaultVnPresentation.tscn",
                "res://addons/majo_runtime/Runtime/UI/SaveLoad/DefaultSaveLoadOverlay.tscn" })
            {
                var scene = ResourceLoader.Load<PackedScene>(path) ?? throw new InvalidOperationException(path);
                scene.Instantiate().Free();
            }
            await RunAsync();
            GD.Print(packaged ? "MAJO_CONSUMER_EXPORT_OK" : "MAJO_CONSUMER_SOURCE_OK");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        var textureRoot = new Node2D();
        AddChild(textureRoot);
        using var loader = new ResourceActorTextureLoader(failOnMissingAssets: true);
        using var settings = new ActorConfiguration();
        var definition = new ActorDefinition("hero", ActorFactoryIds.Character,
            new TextureActorDefinitionData("hero", "characters", "default",
                new Dictionary<string, string> { ["default"] = "res://actor.svg" }));
        using var manager = new ActorManager([definition],
            [new CharacterActorFactory(textureRoot, loader, new CharacterPositionMap(settings))]);
        await manager.InitializeAsync(CancellationToken.None);
        var actor = await manager.CreateAsync("hero");
        if (!actor.Visible || ((TextureCharacterActor)actor).View.Texture is null)
            throw new InvalidOperationException("Default texture actor did not become visible.");
        await manager.ShutdownAsync(CancellationToken.None);

        var customRoot = new Node2D();
        AddChild(customRoot);
        var registry = new ActorFactoryRegistry();
        registry.Register("consumer-custom", ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            context => new ConsumerFactory(context.CharacterRoot, context.CharacterPositions));
        registry.Seal();
        if (!registry.Contains("consumer-custom"))
            throw new InvalidOperationException("Public custom factory registration failed.");

        using var customManager = new ActorManager(
            [new ActorDefinition("custom", "consumer-custom")],
            [new ConsumerFactory(customRoot, new CharacterPositionMap(settings))]);
        await customManager.InitializeAsync(CancellationToken.None);
        var custom = await customManager.CreateAsync("custom");
        if (custom is not ConsumerActor || !custom.Visible)
            throw new InvalidOperationException("Public character base could not create a custom actor.");
        await customManager.ShutdownAsync(CancellationToken.None);
        await RunDialogueAsync();
    }

    private async Task RunDialogueAsync()
    {
        using var imported = ResourceLoader.Load<YarnProject>("res://assets/scripts/main.yarnproject");
        if (imported.Program.Nodes["Start"].Instructions[0].RunCommand?.CommandText == YarnProgramBridge.NodeEntryCommand)
            throw new InvalidOperationException("The upstream importer unexpectedly injected a Majo entry command.");
        var original = imported.CompiledYarnProgramBase64;
        var configuration = MajoProjectConfiguration.CreateDefault();
        var packaged = OS.GetCmdlineUserArgs().Contains("--majo-export-smoke");
        configuration.Dialogue!.HotReload.Enabled = packaged;
        configuration.State!.StorageNamespace = "majo/core-consumer-smoke";
        configuration.Presentation!.Enabled = false;
        configuration.Diagnostics!.ShowFpsOverlay = false;
        var presenter = new ConsumerPresenter();
        var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
        runtime.ConfigureDialoguePresenters = registry => registry.Register(presenter);
        AddChild(runtime);
        await runtime.InitializationCompletion;
        if (packaged && runtime.Host!.Services.Get<IYarnHotReloadService>().Status.State != YarnHotReloadState.Disabled)
            throw new InvalidOperationException("Packed project unexpectedly enabled source hot reload.");
        var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
        await dialogue.StartAsync();
        await dialogue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (string.Join(",", presenter.Lines) != "line:consumer-first,line:consumer-selected,line:consumer-inner,line:consumer-outer,line:consumer-last")
            throw new InvalidOperationException("Independent consumer did not play the expected Yarn branch.");
        var active = runtime.Host.Services.Get<IYarnDialogueBackend>().Runner.YarnProject!;
        if (ReferenceEquals(imported, active) || imported.CompiledYarnProgramBase64 != original ||
            active.Program.Nodes["Start"].Instructions[0].RunCommand?.CommandText != YarnProgramBridge.NodeEntryCommand)
            throw new InvalidOperationException("The Yarn bridge did not isolate its executable program from the imported resource.");
        await runtime.RequestShutdownAsync();
    }

    private sealed class ConsumerPresenter : IDialoguePresenter
    {
        public List<string> Lines { get; } = new();

        public Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
        {
            Lines.Add(line.Id);
            context.RequestNextLine();
            return Task.CompletedTask;
        }

        public Task<DialogueOption?> PresentOptionsAsync(IReadOnlyList<DialogueOption> options, CancellationToken token) =>
            Task.FromResult<DialogueOption?>(options[^1]);
    }

    private sealed class ConsumerFactory(Node2D root, CharacterPositionMap positions) : IActorFactory
    {
        public string FactoryId => "consumer-custom";

        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = new Node2D { Name = definition.Id, Visible = false };
            root.AddChild(view);
            var actor = new ConsumerActor(definition, view, positions);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
    }

    private sealed class ConsumerActor(ActorDefinition definition, Node2D view,
        CharacterPositionMap positions)
        : GodotCharacterActorBase(definition, view, positions,
            CharacterPositionPreset.Center, "default")
    {
        public override void SetAppearance(string appearance) =>
            PersistentWrite(() => Appearance = appearance);

        public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
            string sourceAppearance, string appearance, AnimationSpec animation,
            ITransitionDriver driver, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This smoke actor intentionally supports cut presentation only.");
    }
}

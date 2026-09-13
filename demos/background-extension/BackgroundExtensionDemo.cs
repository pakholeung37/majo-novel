using System;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.BackgroundExtension;

public sealed partial class BackgroundExtensionDemo : Node
{
    public override void _Ready() => Run();
    private async void Run()
    {
        try
        {
            var configuration = MajoProjectConfiguration.CreateDefault();
            configuration.Resources!.RootPath = "res://demos/background-extension/assets";
            configuration.Actors!.ReferenceResolution = new Vector2I(640, 360);
            configuration.Actors.Actors.Add(new ActorDefinitionConfiguration { Id = "landscape", FactoryId = CompositeBackgroundPlugin.FactoryId });
            configuration.Presentation!.Enabled = false;
            configuration.Diagnostics!.EnableOverlay = false;
            configuration.Dialogue!.HotReload.Enabled = false;
            configuration.State!.StorageNamespace = "majo/background-extension";
            var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
            runtime.AddPlugin(new CompositeBackgroundPlugin());
            AddChild(runtime);
            await runtime.InitializationCompletion;
            var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
            var result = await dialogue.Completion;
            if (result.Kind != DialogueCompletionKind.Completed) throw new InvalidOperationException("Background demo did not complete.");
            GD.Print("BACKGROUND_EXTENSION_DEMO_OK");
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--background-auto") >= 0)
            {
                await runtime.RequestShutdownAsync();
                GetTree().Quit();
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}

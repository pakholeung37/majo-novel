#nullable enable

using System;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.Monosaba;

public partial class MonosabaDemoController : Node
{
    public override void _Ready()
    {
        RunDemoAsync();
    }

    private async void RunDemoAsync()
    {
        try
        {
            var configuration = MajoProjectConfiguration.CreateDefault();
            configuration.Resources!.RootPath = "res://demos/monosaba/assets";
            configuration.State!.StorageNamespace = "majo/monosaba";
            configuration.State.GameBuildId = "monosaba-v0.1";
            configuration.Save!.Enabled = true;
            configuration.Diagnostics!.EnableOverlay = true;

            var runtime = new MajoRuntimeBootstrap
            {
                Name = "MonosabaRuntime",
                Configuration = configuration,
            };

            AddChild(runtime);
            await runtime.InitializationCompletion;

            var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
        }
        catch (Exception exception)
        {
            GD.PushError($"Monosaba Demo failed: {exception}");
        }
    }
}

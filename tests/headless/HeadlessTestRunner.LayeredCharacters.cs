#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;
using Majo.Runtime.Stage;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateLayeredCharacterAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://demos/manosaba/assets";
        configuration.Actors!.ReferenceResolution = new Vector2I(2560, 1440);
        configuration.Actors.ScenePositionOffset = new Vector2(0, 10);
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "LayeredCharacterRuntime",
            Configuration = configuration,
        };
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var actors = bootstrap.Host!.Services.Get<IActorManager>();
        Assert(bootstrap.GetNode<SubViewport>("MajoStageViewport").Size ==
                new Vector2I(2560, 1440),
            "Layered character Stage did not retain the configured reference resolution.");
        var preloader = bootstrap.Host.Services.Get<IScriptPreloadManager>();
        await preloader.PrepareAsync(new ScriptPreloadWindow(
            "headless-layered-character",
            new ResourcePreloadManifest(
                "headless-layered-character-resources",
                [ResourcePreloadEntry.Create<Texture2D>(
                    new ResourceKey("characters/Ema/Ema_Angle01"),
                    estimatedBytes: 256L * 1024 * 1024)]),
            [new ActorWarmupRequest(
                "Ema",
                ActorFactoryIds.Character,
                ["Default", "Surprised1"],
                CharacterPositionPreset.Center)]));
        var character = (ICharacterActor)await actors.GetOrCreateAsync(new ActorSpawnRequest(
            "Ema",
            ActorFactoryIds.Character,
            "Default",
            CharacterPositionPreset.Center));
        Assert(!character.Visible &&
                preloader.CurrentWindowId == "headless-layered-character",
            "Layered character preload did not create a dormant ready actor.");
        character.SetVisible(true);
        var layerComposite = character.View.GetNode<CanvasGroup>("LayerComposite");
        var layers = layerComposite.GetChildren().OfType<Sprite2D>().ToArray();
        Assert(character.FactoryId == ActorFactoryIds.LayeredCharacter,
            "Layered character used the wrong actor factory.");
        Assert(layers.Length == 126,
            "Layered character did not create every extracted Ema layer.");
        Assert(layers.All(layer => layer.ZIndex == 0),
            "Layered character layers escaped the CanvasGroup Z batch.");
        var facialLine = layers.Single(layer =>
            layer.Name == "Angle01_Head01_FacialLineDrawing01");
        var normalCheeks = layers.Single(layer =>
            layer.Name == "Angle01_Head01_Facial01_Cheeks01_Cheeks01_Normal");
        Assert(facialLine.Material is ShaderMaterial multiplyMaterial &&
                multiplyMaterial.Shader?.Code.Contains(
                    "mix(vec3(1.0)", System.StringComparison.Ordinal) == true &&
                normalCheeks.Material is null,
            "Layered character did not isolate transparent Multiply pixels from default layers.");

        character.SetScenePosition(new Vector2(45, 0));
        character.SetScale(new Vector2(0.75f, 0.75f));
        Assert(character.Position.IsEqualApprox(new Vector2(1152, 1296)) &&
                character.Scale.IsEqualApprox(new Vector2(0.75f, 0.75f)),
            "Layered character did not map scene percentages, baseline offset, or root scale correctly.");

        var camera = bootstrap.Host.Services.Get<ICameraManager>();
        var cameraPose = new CameraState(new Vector2(37.384f, 74.816f), 3, 10);
        camera.ApplyStableState(cameraPose);
        var cameraBasis = new Transform2D(
            Mathf.DegToRad(cameraPose.RotationDegrees),
            new Vector2(cameraPose.Zoom, cameraPose.Zoom),
            0,
            Vector2.Zero);
        var cameraSceneCenter = new Vector2(
            2560 * cameraPose.Center.X / 100,
            1440 * (1 - cameraPose.Center.Y / 100));
        var expectedCameraTransform = new Transform2D(
            cameraBasis.X,
            cameraBasis.Y,
            new Vector2(1280, 720) - cameraBasis * cameraSceneCenter);
        Assert(bootstrap.GetNode<CanvasLayer>("MajoStageViewport/MajoActors")
                .Transform.IsEqualApprox(expectedCameraTransform),
            "Camera pose did not use the configured Stage reference resolution.");

        var previousAppearance = character.Appearance;
        var actorHost = character.View.GetParent();
        var nodesBeforeTransition = actorHost.GetChildren().ToHashSet();
        var actorChildrenBeforeTransition = character.View.GetChildren().ToHashSet();
        var appearanceTransition = character.ChangeAppearance("Surprised1", 0.2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var normalEye = layers.Single(layer =>
            layer.Name == "Angle01_Head01_Facial01_Eyes01_Eyes01_Normal_Open01");
        var surprisedEye = layers.Single(layer =>
            layer.Name == "Angle01_Head01_Facial01_Eyes01_Eyes01_Surprised_Open01");
        Assert(character.TargetAppearance != previousAppearance &&
                Mathf.IsEqualApprox(layerComposite.SelfModulate.A, 1) &&
                normalEye.Visible && surprisedEye.Visible &&
                normalEye.SelfModulate.A is > 0 and < 1 &&
                surprisedEye.SelfModulate.A is > 0 and < 1,
            "Layered character appearance change did not fade only its changed layers.");
        Assert(actorHost.GetChildren().All(nodesBeforeTransition.Contains) &&
                character.View.GetChildren().All(actorChildrenBeforeTransition.Contains),
            "Layered character appearance change allocated transient scene nodes.");
        await appearanceTransition.Completion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(Mathf.IsEqualApprox(character.Opacity, 1),
            "Layered character appearance change did not finish its fade-in phase.");
        Assert(actorHost.GetChildren().All(nodesBeforeTransition.Contains),
            "Layered character appearance change changed its actor host nodes.");
        Assert(LayerVisible(layers,
                "Angle01_Head01_Facial01_Eyes01_Eyes01_Surprised_Open01") &&
            !LayerVisible(layers,
                "Angle01_Head01_Facial01_Eyes01_Eyes01_Normal_Open01") &&
            LayerVisible(layers,
                "Angle01_Head01_Facial01_Sweat01_Sweat01_01"),
            "Surprised1 did not select Ema's expected eye and sweat layers.");

        character.SetAppearance("Normal1,ArmR3");
        character.SetAppearance("Fearful1");
        Assert(LayerVisible(layers, "Angle01_ArmR_ArmR03") &&
            !LayerVisible(layers, "Angle01_ArmR_ArmR01"),
            "Layered appearance did not preserve the prior ArmR3 mutation.");

        var canonical = character.Appearance;
        character.SetAppearance("Default,ArmR1");
        character.SetAppearance(canonical);
        Assert(LayerVisible(layers, "Angle01_ArmR_ArmR03") &&
            LayerVisible(layers,
                "Angle01_Head01_Facial01_Eyes01_Eyes01_Fearful_Open01"),
            "Canonical layered appearance did not restore a stable actor state.");

        var warden = (ICharacterActor)actors.GetOrCreate(new ActorSpawnRequest(
            "Warden",
            ActorFactoryIds.Character,
            "1",
            CharacterPositionPreset.Center));
        Assert(warden.FactoryId == ActorFactoryIds.Character,
            "Warden should use the ordinary character factory.");
        Assert(((Sprite2D)warden.View).Texture?.GetSize() == new Vector2(1536, 1984),
            "Warden appearance 1 was not loaded from the extracted diced-sprite PNG.");

        bootstrap.QueueFree();
        await bootstrap.ShutdownCompletion;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(character.SceneNode),
            "Layered character node leaked after runtime shutdown.");
    }

    private static bool LayerVisible(Sprite2D[] layers, string name) =>
        layers.Single(layer => layer.Name == name).Visible;
}

#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using Majo.Runtime.State;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Demos.Manosaba;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateTrialCourtBackgroundAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Actors!.ReferenceResolution = new Vector2I(2560, 1440);
        var bootstrap = new MajoRuntimeBootstrap
        {
            Configuration = configuration,
            ConfigureScenePreparation = scenes => scenes.Register("act01_chapter01_trial00", ManosabaCourtPreparation.PrepareAsync),
        };
        using var pixels = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        pixels.Fill(Colors.Red);
        pixels.FillRect(new Rect2I(4, 0, 4, 8), Colors.Blue);
        using var wall = ImageTexture.CreateFromImage(pixels);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        try
        {
            var services = bootstrap.Host!.Services;
            var resources = services.Get<IResourceManager>();
            resources.Register<Texture2D>(new ResourceKey("backgrounds/Court_Wall"), wall);
            var scenes = services.Get<ScenePreparationRegistry>();
            await scenes.PrepareAsync("main", "act01_chapter01_trial00", ScenePreparationReason.Enter, default);
            var injected = resources.Load<Texture2D>(ManosabaCourtPreparation.BackgroundResourceKey);
            Assert(injected is HorizontalRepeatTexture2D { Count: 15 } && injected.GetSize() == new Vector2(15360, 1024),
                "Trial court hook did not register the full logical strip.");
            var metadata = services.Get<DirectionActorDefinitionEnvironment>().Resolver;
            var resolved = metadata.ResolveBackdropAppearance(ManosabaCourtPreparation.BackgroundDefinitionId, null);
            var entries = metadata.GetPreloadResources(new ActorSpawnRequest("main", resolved.Definition.FactoryId, resolved.Appearance)
            { DefinitionId = ManosabaCourtPreparation.BackgroundDefinitionId, UseBackdropResourceConvention = true });
            Assert(entries.Count == 1 && entries[0].Key == ManosabaCourtPreparation.BackgroundResourceKey && entries[0].OverridePath is null,
                "Trial court preload bypassed the scene-injected resource with a file override or wrong appearance key.");
            using (var preload = resources.LeaseAsync<Texture2D>(entries[0].Key, entries[0].OverridePath))
            {
                await preload.Completion;
                Assert(ReferenceEquals(preload.Resource, injected), "Trial court preloading loaded a different resource.");
            }
            var direction = services.Get<IDirectionTransactionCoordinator>();
            await direction.ExecuteAsync(new IDirectionDirective[]
            {
                new BackdropPatch(StageId.Main, new BackdropState(ManosabaCourtPreparation.BackgroundDefinitionId, resolved.Definition.FactoryId, resolved.Appearance)
                { Placement = new(new(-140.625f, 115), new(0, 0), 1.7578125f), Distance = 5 }, DirectionTransitionSpec.Cut),
            }, DirectionCompletionBarrier.Settle);
            var host = services.Get<GodotDirectionRenderHost>();
            var actor = services.Get<GodotDirectionStageBackend>().GetBackgroundActor(StageId.Main)!;
            Assert(actor.CurrentContent!.Root is Sprite2D sprite && ReferenceEquals(sprite.Texture, injected),
                "Direction background did not consume the scene-injected court texture.");
            Assert(host.Backdrop.Raster!.Bounds.Size == injected.GetSize(), "Court background lost its virtual strip bounds.");
            await scenes.PrepareAsync("main", "act01_chapter01_trial00", ScenePreparationReason.Restore, default);
            Assert(ReferenceEquals(resources.Load<Texture2D>(ManosabaCourtPreparation.BackgroundResourceKey), injected),
                "Scene restore must reuse the registered court texture.");
            var contributor = new DirectionStateContributor(direction);
            var saved = await contributor.CaptureAsync(default);
            await direction.ExecuteAsync(new IDirectionDirective[] { new BackdropPatch(StageId.Main, null, DirectionTransitionSpec.Cut) }, DirectionCompletionBarrier.Settle);
            var context = new RestoreContext(contributor.Scope, new Dictionary<string, StateRecord> { [contributor.Key] = saved });
            await using (var restore = await contributor.PrepareRestoreAsync(saved, context, default)) restore.Commit();
            Assert(host.Backdrop.Raster is not null && direction.State.Stages[StageId.Main].Backdrop!.DefinitionId == ManosabaCourtPreparation.BackgroundDefinitionId,
                "Background restore required a manually registered actor definition.");
            if (DisplayServer.GetName() != "headless")
            {
                await host.Backdrop.Raster!.ReadyAsync(default);
                using var image = host.Backdrop.Raster.Texture.GetImage();
                foreach (var panel in new[] { 0, 7, 14 })
                {
                    AssertColor(image.GetPixel(panel * 1024 + 128, 512), Colors.Red, "Injected court panel left half");
                    AssertColor(image.GetPixel(panel * 1024 + 896, 512), Colors.Blue, "Injected court panel right half");
                }
            }
            GD.Print("TRIAL00_BACKGROUND_OK: injected preload, actor rendering, 15 panels and restore reuse.");
        }
        finally { await bootstrap.RequestShutdownAsync(); bootstrap.QueueFree(); }
    }
}

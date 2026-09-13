using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private static IEnumerable<ActorDefinition> BackgroundFixtureDefinitions()
    {
        foreach (var name in new[] { "background_dark", "background_light" })
        {
            var id = $"tests/headless/fixtures/{name}.svg";
            yield return new ActorDefinition(id, ActorFactoryIds.Background,
                new TextureActorDefinitionData(id, "backgrounds", "Default",
                    new Dictionary<string, string> { ["Default"] = "res://" + id }));
        }
    }

    // Low-level texture probes use the same direct presentation path as BackgroundActor.
    private static GodotBackgroundRaster CreateBackgroundRaster(GodotDirectionRenderHost host, Texture2D texture)
    {
        using var content = GodotBackgroundContent.FromTexture(texture);
        var raster = new GodotBackgroundRaster(host.StageViewport, content);
        host.StageViewport.TreeExiting += raster.Dispose;
        return raster;
    }
}

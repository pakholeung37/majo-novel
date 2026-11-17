using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class DirectionMetadataTests
{
    [Fact]
    public void DirectionMetadataRejectsCorruptPayloads()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => YarnProjectDirectionMetadata.Deserialize("{broken"));
        Assert.Throws<InvalidOperationException>(() => YarnProjectDirectionMetadata.Deserialize("null"));
        Assert.Throws<InvalidOperationException>(() => YarnProjectDirectionMetadata.Deserialize("[{}]"));
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
            [new YarnCompilationDiagnostic("warning", "MAJO_TEST", "main.yarn", 4, 1, 4, 8, "示例")])
        {
            SceneEntry = new SceneEntryMetadata("room", "main.yarn", 2, 1),
            CameraPresets = [new DirectionCameraPreset("close", "focus", 2, 10)
            {
                Options = new Dictionary<string, string> { ["extends"] = "base", ["parallax"] = "strong" },
            }],
        };
        var json = YarnProjectDirectionMetadata.Serialize(new[] { module });

        var roundTrip = Assert.Single(YarnProjectDirectionMetadata.Deserialize(json));
        Assert.Equal(module.NodeId, roundTrip.NodeId);
        Assert.Equal(module.Version, roundTrip.Version);
        Assert.Equal(module.Symbols.Stages, roundTrip.Symbols.Stages);
        Assert.Equal(module.Resources, roundTrip.Resources);
        Assert.Equal(module.Imports, roundTrip.Imports);
        Assert.Equal(module.MarkDefinitions, roundTrip.MarkDefinitions);
        Assert.Equal(module.SourceMaps, roundTrip.SourceMaps);
        Assert.Equal(module.Diagnostics, roundTrip.Diagnostics);
        Assert.Equal(module.SceneEntry, roundTrip.SceneEntry);
        var preset = Assert.Single(roundTrip.CameraPresets);
        Assert.Equal("focus", preset.Center);
        Assert.Equal("base", preset.Options!["extends"]);
        Assert.Equal("strong", preset.Options["parallax"]);
    }

    [Fact]
    public void LegacyMetadataWithoutOptionalFieldsRemainsReadable()
    {
        var module = Assert.Single(YarnProjectDirectionMetadata.Deserialize("[{\"NodeId\":\"Opening\",\"Version\":3}]"));
        Assert.Equal(3, module.Version);
        Assert.Null(module.SceneEntry);
        Assert.Empty(module.CameraPresets);
        Assert.Empty(module.Diagnostics);
        Assert.Empty(module.Symbols.Stages);
        Assert.Empty(module.Resources);
    }

    [Fact]
    public void PreloadClaimsDeduplicateByKindAndId()
    {
        var manifest = new DirectionPreloadManifest([
            new(DirectionResourceKind.Backdrop, "room", "a.yarn", 2, 1),
            new(DirectionResourceKind.Backdrop, "room", "b.yarn", 5, 2),
            new(DirectionResourceKind.ActorAppearance, "room", "b.yarn", 6, 2),
        ]);
        Assert.Equal(2, manifest.Claims.Count);
        Assert.Contains(new DirectionResourceClaim(DirectionResourceKind.Backdrop, "room"), manifest.Claims);
        Assert.Contains(new DirectionResourceClaim(DirectionResourceKind.ActorAppearance, "room"), manifest.Claims);
        Assert.Equal("room", Assert.Single(manifest.SemanticIds));
    }
}

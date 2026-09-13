using Majo.Runtime.Actors;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class BackdropConventionTests
{
    [Theory]
    [InlineData("backgrounds", "court", "backgrounds/court")]
    [InlineData("backgrounds", "backgrounds/court", "backgrounds/court")]
    [InlineData("sets", "chapter/court", "sets/chapter/court")]
    [InlineData("", "court", "court")]
    public void MissingDefinitionsUseOriginalLogicalKeyForPreparePreloadAndRestore(string root, string id, string key)
    {
        var resolver = new ActorDefinitionResolver([], [new TextureMetadata()], new(BackgroundsPath: root));
        var resolved = resolver.ResolveBackdropAppearance(id);
        Assert.Equal("Default", resolved.Appearance);
        Assert.Equal(key, resolved.Definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(id, resolved.Appearance).Path);
        var request = new ActorSpawnRequest("main", "background", resolved.Appearance, requiredFactoryId: "background")
        { DefinitionId = id, UseBackdropResourceConvention = true };
        var entry = Assert.Single(resolver.GetPreloadResources(request));
        Assert.Equal(key, entry.Key.Path);
        Assert.Null(entry.OverridePath);
        Assert.Equal(key + "/Night", resolver.ResolveBackdropAppearance(id, "Night").Definition
            .GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(id, "Night").Path);
        var restored = resolver.ResolveBackdropAppearance(id, resolved.Appearance, requiredFactoryId: "background");
        Assert.Equal(key, restored.Definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(id, restored.Appearance).Path);
        Assert.Throws<ActorFactoryConflictException>(() => resolver.ResolveBackdropAppearance(id, requiredFactoryId: "removed-custom"));
    }

    [Fact]
    public void ExplicitDefinitionsKeepTheirMappingsAndFailures()
    {
        var data = new TextureActorDefinitionData("court", "backgrounds", "Day", [new("Day", "res://custom.svg")]);
        var resolver = new ActorDefinitionResolver([new("court", "background", data)], [new TextureMetadata()]);
        var resolved = resolver.ResolveBackdropAppearance("court");
        Assert.Same(data, resolved.Definition.FactoryData);
        Assert.Equal("Day", resolved.Appearance);
        Assert.Equal("res://custom.svg", data.GetAppearanceOverride(resolved.Appearance));
        var invalid = new ActorDefinitionResolver([new("court", "missing-factory")], [new TextureMetadata()]);
        Assert.Throws<ActorFactoryNotFoundException>(() => invalid.ResolveBackdropAppearance("court"));
        var excluded = new ActorDefinitionResolver([], [new TextureMetadata()], unsupportedDefinitions: [new("court", "runtime-only")]);
        Assert.Throws<ActorDefinitionException>(() => excluded.ResolveBackdropAppearance("court"));
    }

    private sealed class TextureMetadata : IActorFactory
    {
        public string FactoryId => "background";
        public string GetInitialAppearance(ActorDefinition definition) => definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance)
        {
            var data = definition.GetFactoryData<TextureActorDefinitionData>();
            return [ResourcePreloadEntry.Create<object>(data.GetAppearanceResourceKey(definition.DefinitionId, appearance), data.GetAppearanceOverride(appearance))];
        }
        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) => throw new NotSupportedException();
    }
}

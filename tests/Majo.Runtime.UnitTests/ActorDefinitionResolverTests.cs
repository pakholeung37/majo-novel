using Majo.Runtime.Actors;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorDefinitionResolverTests
{
    [Fact]
    public void ExplicitDefinitionOverridesFallbackAndKeepsAssetIdentity()
    {
        var data = new object();
        var resolver = new ActorDefinitionResolver([new("Court", "custom", data)], [new MetadataFactory()]);
        var result = resolver.ResolveExplicitAppearance("Court");
        Assert.Equal("main", result.Definition.Id);
        Assert.Equal("Court", result.Definition.DefinitionId);
        Assert.Equal("custom", result.Factory.FactoryId);
        Assert.Equal("Day", result.Appearance);
        Assert.Same(data, result.Definition.FactoryData);
    }

    [Theory]
    [InlineData(null, "Night", "Day")]
    [InlineData("Night", "Day", "Night")]
    [InlineData("+Rain", "Night", "Night+Rain")]
    [InlineData("+Rain", null, "Day+Rain")]
    public void AppearanceResolutionUsesExplicitBaselineAndOmissionAlwaysUsesDefault(
        string? appearance, string? previous, string expected)
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")], [new MetadataFactory()]);
        Assert.Equal(expected, resolver.ResolveExplicitAppearance("Court", appearance, previous).Appearance);
    }

    [Fact]
    public void StrictResolutionNeverFallsBackToTextureConventions()
    {
        var resolver = new ActorDefinitionResolver([], [new MetadataFactory(ActorFactoryIds.Background)]);
        Assert.Throws<ActorDefinitionException>(() => resolver.ResolveExplicitAppearance("missing"));
        Assert.False(resolver.CanResolve(new("main", ActorFactoryIds.Background, "Day")
        { DefinitionId = "missing", RequireExplicitDefinition = true }));
        Assert.True(resolver.CanResolve(new("missing", ActorFactoryIds.Background, "Day")));
    }

    [Fact]
    public void ExcludedDomainDefinitionCannotBeInferredOrPreloaded()
    {
        var resolver = new ActorDefinitionResolver([], [new MetadataFactory()],
            unsupportedDefinitions: [new("Court", "custom")]);
        Assert.Throws<ActorDefinitionException>(() => resolver.ResolveExplicitAppearance("Court"));
        Assert.Throws<ActorDefinitionException>(() => resolver.GetPreloadResources(
            new("Court", "custom", "Day")));
        Assert.False(resolver.CanResolve(new("Court", "custom", "Day")));
    }

    [Fact]
    public void RestoreFactoryConstraintIsEnforced()
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")], [new MetadataFactory()]);
        Assert.Throws<ActorFactoryConflictException>(() =>
            resolver.ResolveExplicitAppearance("Court", requiredFactoryId: "old-factory"));
        Assert.Equal("custom", resolver.ResolveExplicitAppearance("Court",
            requiredFactoryId: "custom").Factory.FactoryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingDefaultIsDefinitionErrorEvenWithExplicitAppearance(string? initial)
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")],
            [new MetadataFactory(initial: initial)]);
        Assert.Throws<ActorDefinitionException>(() => resolver.ResolveExplicitAppearance("Court", "Night"));
    }

    [Fact]
    public void EmptyResolvedAppearanceIsRejected()
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")], [new MetadataFactory()]);
        Assert.Throws<ActorDefinitionException>(() => resolver.ResolveExplicitAppearance("Court", " "));
    }

    [Fact]
    public void MissingFactoryFailsBeforeAnyInstanceCreation()
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")], []);
        Assert.Throws<ActorFactoryNotFoundException>(() => resolver.ResolveExplicitAppearance("Court"));
        Assert.False(resolver.CanResolve(new("Court", "custom", "Day")));
    }

    [Fact]
    public async Task ManagerUsesSharedResolverAndHonorsLateFactoryRegistration()
    {
        var factory = new MetadataFactory();
        using var manager = new ActorManager([new("Court", "custom")]);
        manager.RegisterFactory(factory);
        await manager.InitializeAsync(default);
        Assert.True(manager.CanCreate(new("Court", ActorFactoryIds.Background, "Night")));
        var result = manager.DefinitionResolver.ResolveExplicitAppearance("Court", "Night");
        var resources = manager.GetPreloadResources(new("main", ActorFactoryIds.Background, "Night")
        { DefinitionId = "Court", RequireExplicitDefinition = true });
        Assert.Equal(result.Definition.DefinitionId + "/" + result.Appearance, Assert.Single(resources).Key.Path);
        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(default);
    }

    [Fact]
    public void RequiredFactoryAlsoConstrainsConventionalFallback()
    {
        var resolver = new ActorDefinitionResolver([], [new MetadataFactory(ActorFactoryIds.Background)]);
        var request = new ActorSpawnRequest("missing", ActorFactoryIds.Background, "Day", requiredFactoryId: "custom");
        Assert.Throws<ActorFactoryConflictException>(() => resolver.Resolve(request));
        Assert.False(resolver.CanResolve(request));
    }

    [Fact]
    public void DefaultPreloadUsesFactoryMetadataWithoutCreatingActor()
    {
        var resolver = new ActorDefinitionResolver([new("Court", "custom")], [new MetadataFactory()]);
        var entries = resolver.GetPreloadResources(new("main", ActorFactoryIds.Background, "ignored")
        { DefinitionId = "Court", RequireExplicitDefinition = true, UseDefinitionDefaultAppearance = true });
        Assert.Equal("Court/Day", Assert.Single(entries).Key.Path);
    }

    private sealed class MetadataFactory(string id = "custom", string? initial = "Day") : IActorFactory
    {
        public string FactoryId => id;
        public string? GetInitialAppearance(ActorDefinition definition) => initial;
        public string ResolveAppearance(ActorDefinition definition, string sourceAppearance, string appearance) =>
            appearance.StartsWith('+') ? sourceAppearance + appearance : appearance;
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
            [ResourcePreloadEntry.Create<object>(new ResourceKey(definition.DefinitionId + "/" + appearance))];
        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata resolution must not create actors.");
    }
}

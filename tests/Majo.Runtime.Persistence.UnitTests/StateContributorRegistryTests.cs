using Majo.Runtime.State;
using Majo.Runtime.Persistence.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.Persistence.UnitTests;

public sealed class StateContributorRegistryTests
{

    [Fact]
    public void RegistryBuildsStableTopologicalOrderAndRejectsInvalidGraphs()
    {
        var variables = new MetadataContributor("variables", []);
        var actors = new MetadataContributor("actors", ["variables"]);
        var script = new MetadataContributor(
            "script",
            ["variables", "actors"]);

        var ordered = new StateContributorRegistry()
            .Register(script)
            .Register(actors)
            .Register(variables)
            .Build();

        Assert.Equal(["variables", "actors", "script"], ordered.Select(item => item.Key));
        Assert.Throws<DuplicateStateContributorException>(() =>
            new StateContributorRegistry().Register(variables).Register(variables));
        Assert.Throws<StateRuntimeException>(() =>
            new StateContributorRegistry()
                .Register(new MetadataContributor("orphan", ["missing"]))
                .Build());
        Assert.Throws<StateRuntimeException>(() =>
            new StateContributorRegistry()
                .Register(new MetadataContributor("a", ["b"]))
                .Register(new MetadataContributor("b", ["a"]))
                .Build());
    }
}

using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class DomainBoundaryTests
{
    [Fact]
    public void NodeMetadataAndEntryRequestsBelongToTheIndependentDomain()
    {
        var domain = typeof(DirectionPlanner).Assembly;
        Assert.Equal(domain, typeof(NodeDirectionModule).Assembly);
        Assert.Equal(domain, typeof(NodeEntryRequest).Assembly);
        Assert.Equal(domain, typeof(YarnProjectDirectionMetadata).Assembly);
        Assert.Equal(domain, typeof(YarnCompilationDiagnostic).Assembly);
    }

    [Fact]
    public void DomainAndCoreReferenceOnlyPlatformAssembliesAndCoreContracts()
    {
        foreach (var assembly in new[] { typeof(DirectionPlanner).Assembly, typeof(IRuntimeService).Assembly })
            Assert.All(assembly.GetReferencedAssemblies(), reference =>
                Assert.True(reference.Name == "Majo.Runtime.Core" || reference.Name == "netstandard" ||
                    reference.Name!.StartsWith("System.", StringComparison.Ordinal),
                    $"Engine-independent assembly '{assembly.GetName().Name}' references '{reference.Name}'."));
    }
}

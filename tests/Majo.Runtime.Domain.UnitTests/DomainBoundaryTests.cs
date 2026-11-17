using Majo.Runtime.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class DomainBoundaryTests
{
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

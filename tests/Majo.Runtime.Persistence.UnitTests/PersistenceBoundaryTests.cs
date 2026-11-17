using Majo.Runtime.Core;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.Persistence.UnitTests;

public sealed class PersistenceBoundaryTests
{
    [Fact]
    public void PersistenceReferencesOnlyPlatformAssembliesAndCoreContracts()
    {
        var assembly = typeof(CanonicalJsonStateSerializer).Assembly;
        Assert.Equal("Majo.Runtime.Persistence", assembly.GetName().Name);
        Assert.NotEqual(typeof(IRuntimeService).Assembly, assembly);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name == "Majo.Runtime.Core" || reference.Name == "netstandard" ||
                reference.Name!.StartsWith("System.", StringComparison.Ordinal),
                $"Persistence assembly references '{reference.Name}'."));
    }
}

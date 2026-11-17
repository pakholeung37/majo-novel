using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.Resources.UnitTests;

public sealed class ResourceBoundaryTests
{
    [Fact]
    public void ResourcesReferenceOnlyPlatformAssembliesAndCoreContracts()
    {
        var assembly = typeof(ResourceManager).Assembly;
        Assert.Equal("Majo.Runtime.Resources", assembly.GetName().Name);
        Assert.NotEqual(typeof(IRuntimeService).Assembly, assembly);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name == "Majo.Runtime.Core" || reference.Name == "netstandard" ||
                reference.Name!.StartsWith("System.", StringComparison.Ordinal),
                $"Resource assembly references '{reference.Name}'."));
    }
}

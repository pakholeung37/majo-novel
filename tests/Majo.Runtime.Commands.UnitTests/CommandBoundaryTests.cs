using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Xunit;

namespace Majo.Runtime.Commands.UnitTests;

public sealed class CommandBoundaryTests
{
    [Fact]
    public void CommandsReferenceOnlyPlatformAssembliesAndCoreContracts()
    {
        var assembly = typeof(RuntimeCommandRegistry).Assembly;
        Assert.Equal("Majo.Runtime.Commands", assembly.GetName().Name);
        Assert.Equal(assembly, typeof(IYarnCommandAdapter).Assembly);
        Assert.Equal(assembly, typeof(RuntimeCommandActivity).Assembly);
        Assert.NotEqual(typeof(IRuntimeService).Assembly, assembly);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name == "Majo.Runtime.Core" || reference.Name == "netstandard" ||
                reference.Name!.StartsWith("System.", StringComparison.Ordinal),
                $"Command assembly references '{reference.Name}'."));
    }
}

using Majo.Runtime;
using Majo.Runtime.Core;
using Xunit;
using YarnSpinnerGodot;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeAssemblyBoundaryTests
{
    [Fact]
    public void RuntimeAndTestsHaveNoGameDemoOrHeadlessAssemblyDependency()
    {
        var runtime = typeof(MajoRuntimeBootstrap).Assembly;
        Assert.Equal("Majo.Runtime.Godot", runtime.GetName().Name);
        Assert.NotEqual(typeof(IRuntimeService).Assembly, runtime);
        foreach (var assembly in new[] { runtime, typeof(RuntimeAssemblyBoundaryTests).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "Majo Novel" or "Majo.Demos" or "Majo.Runtime.Headless");
    }

    [Fact]
    public void UpstreamGodotYarnIsSeparateAndDoesNotReferenceMajoRuntime()
    {
        var yarn = typeof(DialogueRunner).Assembly;
        Assert.Equal("YarnSpinner.Godot", yarn.GetName().Name);
        Assert.DoesNotContain(yarn.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Majo.", StringComparison.Ordinal));
    }
}

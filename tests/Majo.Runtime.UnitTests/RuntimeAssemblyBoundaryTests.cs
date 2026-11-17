using Majo.Runtime;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Input;
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
        foreach (var assembly in new[] { runtime, typeof(IInputManager).Assembly, typeof(YarnDialogueBackend).Assembly, typeof(RuntimeAssemblyBoundaryTests).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "Majo Novel" or "Majo.Demos" or "Majo.Runtime.Headless");
    }

    [Fact]
    public void EngineServicesHaveNoYarnOrCompositionDependency()
    {
        var engine = typeof(IInputManager).Assembly;
        Assert.Equal("Majo.Runtime.Engine", engine.GetName().Name);
        Assert.All(engine.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name is "GodotSharp" or "GodotSharpEditor" or "netstandard" or "Microsoft.Win32.Primitives" ||
                reference.Name!.StartsWith("System.", StringComparison.Ordinal) ||
                reference.Name is "Majo.Runtime.Core" or "Majo.Runtime.Domain" or "Majo.Runtime.Commands" or
                    "Majo.Runtime.Persistence" or "Majo.Runtime.Resources",
                $"General engine services reference '{reference.Name}'."));
    }

    [Fact]
    public void YarnAdapterDependsOnEngineServicesWithoutComposition()
    {
        var adapter = typeof(YarnDialogueBackend).Assembly;
        Assert.Equal("Majo.Runtime.Yarn", adapter.GetName().Name);
        var references = adapter.GetReferencedAssemblies();
        Assert.Contains(references, reference => reference.Name == "Majo.Runtime.Engine");
        Assert.Contains(references, reference => reference.Name == "YarnSpinner.Godot");
        Assert.DoesNotContain(references, reference => reference.Name == "Majo.Runtime.Godot");
        Assert.DoesNotContain(typeof(IInputManager).Assembly.GetCustomAttributesData(), attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Majo.Runtime.Yarn");
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

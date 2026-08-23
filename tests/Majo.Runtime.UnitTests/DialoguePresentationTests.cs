using System.Reflection;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DialoguePresentationTests
{
    [Fact]
    public void StablePresentationApiDoesNotExposeGodotOrYarnTypes()
    {
        var stableTypes = typeof(IDialoguePresentation).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Majo.Runtime.Dialogue.Presentation");

        foreach (var type in stableTypes)
        {
            foreach (var memberType in GetPublicMemberTypes(type))
            {
                var namespaceName = Unwrap(memberType).Namespace ?? string.Empty;
                Assert.DoesNotContain("Godot", namespaceName, StringComparison.Ordinal);
                Assert.DoesNotContain("Yarn", namespaceName, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MarkupHandlerRegistryPreservesOrderAndRejectsDuplicateInstance()
    {
        var first = new ProbeMarkupHandler();
        var second = new ProbeMarkupHandler();
        var registry = new DialogueMarkupHandlerRegistry()
            .Register(first)
            .Register(second);

        Assert.Equal([first, second], registry.Handlers);
        Assert.Throws<DialogueException>(() => registry.Register(first));
    }

    private static IEnumerable<Type> GetPublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        if (type.IsGenericType)
        {
            return Unwrap(type.GetGenericArguments()[0]);
        }

        return type;
    }

    private sealed class ProbeMarkupHandler : IDialogueMarkupHandler;
}

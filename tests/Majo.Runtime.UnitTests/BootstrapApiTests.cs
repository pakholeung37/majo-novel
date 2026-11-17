using System.Reflection;
using Majo.Runtime.Dialogue;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class BootstrapApiTests
{
    [Fact]
    public void StableDialogueApiDoesNotExposeYarnTypes()
    {
        var assembly = typeof(IDialoguePlayer).Assembly;
        var types = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Majo.Runtime.Dialogue")
            .Append(typeof(MajoRuntimeBootstrap));

        foreach (var type in types)
        {
            foreach (var memberType in GetPublicMemberTypes(type))
            {
                Assert.False(
                    IsYarnType(memberType),
                    $"Public member on {type.FullName} exposes Yarn type {memberType.FullName}.");
            }
        }
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

        foreach (var eventInfo in type.GetEvents(flags))
        {
            if (eventInfo.EventHandlerType is not null)
            {
                yield return eventInfo.EventHandlerType;
            }
        }
    }

    private static bool IsYarnType(Type type)
    {
        if (type.IsArray)
        {
            return IsYarnType(type.GetElementType()!);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(IsYarnType))
        {
            return true;
        }

        var namespaceName = type.Namespace ?? string.Empty;
        return namespaceName == "Yarn" ||
            namespaceName.StartsWith("Yarn.", StringComparison.Ordinal) ||
            namespaceName == "YarnSpinnerGodot" ||
            namespaceName.StartsWith("YarnSpinnerGodot.", StringComparison.Ordinal);
    }

}

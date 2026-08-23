using System.Reflection;
using Majo.Runtime.Auto;
using Majo.Runtime.History;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ScriptRuntimeTests
{
    [Fact]
    public void ScriptIdentityAndCheckpointNormalizeStableValues()
    {
        var id = new ScriptId(" chapter ", " Start ");
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            " fingerprint ",
            new ScriptPlaybackSpot(id, " line:intro "),
            ["Maya", "7", "True"]);

        Assert.Equal("chapter/Start", id.ToString());
        Assert.Equal("fingerprint", checkpoint.ProgramFingerprint);
        Assert.Equal("line:intro", checkpoint.Spot.LineId);
        Assert.Equal(["Maya", "7", "True"], checkpoint.ResolvedSubstitutions);
    }

    [Fact]
    public void ScriptDescriptorRejectsDuplicateStableLineIds()
    {
        var exception = Assert.Throws<ScriptCheckpointException>(() =>
            new ScriptDescriptor(
                new ScriptId("chapter", "Start"),
                "fingerprint",
                ["line:duplicate", "line:duplicate"]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptStateAutoAndHistoryPublicContractsDoNotExposeBackendTypes()
    {
        var stableNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            typeof(IScriptPlayback).Namespace!,
            typeof(IStateManager).Namespace!,
            typeof(IAutoManager).Namespace!,
            typeof(IHistoryManager).Namespace!,
        };
        var types = typeof(IScriptPlayback).Assembly.GetExportedTypes()
            .Where(type => type.Namespace is not null &&
                stableNamespaces.Contains(type.Namespace));

        foreach (var type in types)
        {
            foreach (var memberType in PublicMemberTypes(type).SelectMany(Expand))
            {
                Assert.False(
                    memberType.Namespace?.StartsWith("Yarn", StringComparison.Ordinal) == true ||
                    memberType.Namespace?.StartsWith("Godot", StringComparison.Ordinal) == true,
                    $"Public member on {type.FullName} exposes backend type " +
                    $"{memberType.FullName}.");
            }
        }
    }

    private static IEnumerable<Type> PublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

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

    private static IEnumerable<Type> Expand(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument))
            {
                yield return nested;
            }
        }
    }
}

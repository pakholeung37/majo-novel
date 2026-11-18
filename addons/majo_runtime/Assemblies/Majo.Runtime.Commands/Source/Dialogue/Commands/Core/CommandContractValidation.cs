#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

internal static class CommandContractValidation
{
    internal static string MemberPath(CommandParameterDeclaration parameter) =>
        string.IsNullOrEmpty(parameter.GroupPath) ? parameter.MemberName : $"{parameter.GroupPath}.{parameter.MemberName}";

    internal static CommandGroupDeclaration SnapshotGroup(CommandGroupDeclaration group) => group with
    {
        Parameters = Array.AsReadOnly((group.Parameters ?? Array.Empty<CommandParameterDeclaration>()).ToArray()),
        SubGroups = Array.AsReadOnly((group.SubGroups ?? Array.Empty<CommandGroupDeclaration>()).Select(SnapshotGroup).ToArray()),
        ParameterSets = Array.AsReadOnly((group.ParameterSets ?? Array.Empty<CommandParameterSetSpec>())
            .Select(s => s with { Parameters = Array.AsReadOnly(s.Parameters.ToArray()) }).ToArray()),
    };

    internal static void ValidateParameters(string command, IReadOnlyList<CommandParameterDeclaration> parameters)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryCount = 0;
        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Any(c => char.IsWhiteSpace(c) || c == ':') ||
                string.IsNullOrWhiteSpace(p.MemberName) || !names.Add(p.Name) || !members.Add(MemberPath(p)))
                Fail(command, p, "has an invalid or duplicate name/member path");
            if (p.Primary && ++primaryCount > 1) Fail(command, p, "is a second primary parameter");
            if (p.Primary && !p.Required || p.Required == p.IsCommandValue || p.Required && p.HasDefault)
                Fail(command, p, "has inconsistent required/presence/default declarations");
            if (Nullable.GetUnderlyingType(p.ValueType) is not null || p.ValueType.ContainsGenericParameters)
                Fail(command, p, "must have a closed, non-nullable value type");
            if (p.Minimum.HasValue && (!double.IsFinite(p.Minimum.Value) || !IsNumeric(p.ValueType)))
                Fail(command, p, "Minimum requires a numeric type and a finite bound");
            if (p.HasDefault && p.DefaultText is null && p.DefaultValue is null)
                Fail(command, p, "has a null contract default");
            if (p.DefaultText is not null && p.DefaultValue is not null)
                Fail(command, p, "cannot specify both DefaultText and DefaultValue");
        }
    }

    internal static void ValidateValue(string command, CommandParameterDeclaration p, object? value, CommandFailurePhase phase)
    {
        if (value is null || !p.ValueType.IsInstanceOfType(value))
            throw new RuntimeCommandException(command, $"Parameter '{p.Name}' expected '{p.ValueType.FullName}', got '{value?.GetType().FullName ?? "null"}'.", phase: phase);
    }

    internal static IReadOnlyList<CommandParameterSetSpec> ResolveSets(ICommandDefinition definition)
    {
        var members = definition.FlattenedParameters.ToDictionary(MemberPath, StringComparer.OrdinalIgnoreCase);
        var names = definition.FlattenedParameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(definition.ParameterSets.Select(set =>
        {
            if (!Enum.IsDefined(set.Rule) || set.Parameters.Count < 2)
                throw new RuntimeCommandException(definition.Name, "Invalid parameter set rule or member count.", phase: CommandFailurePhase.Schema);
            var resolved = set.Parameters.Select(key =>
            {
                // Member references and input names are resolved once, never during execution.
                if (members.TryGetValue(key, out var member)) return member.Name;
                if (names.TryGetValue(key, out var named)) return named.Name;
                throw new RuntimeCommandException(definition.Name, $"Unknown parameter set member '{key}'.", phase: CommandFailurePhase.Schema);
            }).ToArray();
            if (resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolved.Length)
                throw new RuntimeCommandException(definition.Name, "Parameter set contains duplicate members.", phase: CommandFailurePhase.Schema);
            return set with { Parameters = Array.AsReadOnly(resolved) };
        }).ToArray());
    }

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static void Fail(string command, CommandParameterDeclaration p, string message) =>
        throw new RuntimeCommandException(command, $"Parameter '{p.Name}' {message}.", phase: CommandFailurePhase.Schema);
}

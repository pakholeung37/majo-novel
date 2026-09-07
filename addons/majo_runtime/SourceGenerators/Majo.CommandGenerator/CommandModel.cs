#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Majo.CommandGenerator;

public abstract class ArgumentMemberModel
{
    public abstract string MemberName { get; }
}

public sealed class CommandModel
{
    public CommandModel(
        string name,
        string? category,
        string? usage,
        string containingNamespace,
        string className,
        string argumentsFullTypeName,
        IReadOnlyList<ArgumentMemberModel> members,
        IReadOnlyList<ParameterSetModel> parameterSets,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Name = name;
        Category = category;
        Usage = usage;
        ContainingNamespace = containingNamespace;
        ClassName = className;
        ArgumentsFullTypeName = argumentsFullTypeName;
        Members = members;
        ParameterSets = parameterSets;
        Diagnostics = diagnostics;
    }

    public string Name { get; }
    public string? Category { get; }
    public string? Usage { get; }
    public string ContainingNamespace { get; }
    public string ClassName { get; }
    public string ArgumentsFullTypeName { get; }
    public IReadOnlyList<ArgumentMemberModel> Members { get; }
    public IReadOnlyList<ParameterModel> Parameters => Members.OfType<ParameterModel>().ToList();
    public IReadOnlyList<GroupModel> Groups => Members.OfType<GroupModel>().ToList();
    public IReadOnlyList<ParameterSetModel> ParameterSets { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Diagnostics.Count > 0;
}

public sealed class ParameterModel : ArgumentMemberModel
{
    public ParameterModel(
        string name,
        string memberName,
        int order,
        bool isPrimary,
        bool isRequired,
        string valueTypeFullName,
        bool isCommandValue,
        string valueHint,
        bool hasDefault,
        string? defaultText,
        double? minimum,
        string? converterTypeFullName,
        string? groupPath = null)
    {
        Name = name;
        MemberName = memberName;
        Order = order;
        IsPrimary = isPrimary;
        IsRequired = isRequired;
        ValueTypeFullName = valueTypeFullName;
        IsCommandValue = isCommandValue;
        ValueHint = valueHint;
        HasDefault = hasDefault;
        DefaultText = defaultText;
        Minimum = minimum;
        ConverterTypeFullName = converterTypeFullName;
        GroupPath = groupPath;
    }

    public string Name { get; }
    public override string MemberName { get; }
    public int Order { get; }
    public bool IsPrimary { get; }
    public bool IsRequired { get; }
    public string ValueTypeFullName { get; }
    public bool IsCommandValue { get; }
    public string ValueHint { get; }
    public bool HasDefault { get; }
    public string? DefaultText { get; }
    public double? Minimum { get; }
    public string? ConverterTypeFullName { get; }
    public string? GroupPath { get; }
    public bool HasParameterlessConverter { get; set; }
}

public sealed class GroupModel : ArgumentMemberModel
{
    public GroupModel(
        string name,
        string memberName,
        int order,
        string groupTypeFullName,
        string? configurationTypeFullName,
        IReadOnlyList<ArgumentMemberModel> members,
        IReadOnlyList<ParameterSetModel> parameterSets)
    {
        Name = name;
        MemberName = memberName;
        Order = order;
        GroupTypeFullName = groupTypeFullName;
        ConfigurationTypeFullName = configurationTypeFullName;
        ParameterSets = parameterSets;
        Members = members;
    }

    public string Name { get; }
    public override string MemberName { get; }
    public int Order { get; }
    public string GroupTypeFullName { get; }
    public string? ConfigurationTypeFullName { get; }
    public IReadOnlyList<ArgumentMemberModel> Members { get; }
    public IReadOnlyList<ParameterModel> Parameters => Members.OfType<ParameterModel>().ToList();
    public IReadOnlyList<ParameterSetModel> ParameterSets { get; }
    public IReadOnlyList<GroupModel> SubGroups => Members.OfType<GroupModel>().ToList();
}

public sealed class ParameterSetModel
{
    public ParameterSetModel(string ruleName, IReadOnlyList<string> parameterNames)
    {
        RuleName = ruleName;
        ParameterNames = parameterNames;
    }

    public string RuleName { get; }
    public IReadOnlyList<string> ParameterNames { get; }
}

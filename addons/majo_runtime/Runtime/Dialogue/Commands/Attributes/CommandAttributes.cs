#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public string? Category { get; set; }

    public string? Usage { get; set; }
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class PrimaryAttribute : Attribute
{
    public Type? Converter { get; set; }
    public double Minimum { get; set; } = double.NaN;
    public int Order { get; set; } = -1;
    public string? Name { get; set; }

    public string? ValueHint { get; set; }
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class OptionAttribute : Attribute
{
    public string? Name { get; set; }

    public string? ValueHint { get; set; }

    public string? DefaultText { get; set; }

    public double Minimum { get; set; } = double.NaN;

    public Type? Converter { get; set; }

    public int Order { get; set; } = -1;
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class GroupAttribute : Attribute
{
    public string? Name { get; set; }

    public Type? Configuration { get; set; }

    public int Order { get; set; } = -1;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class AtMostOneAttribute : Attribute
{
    public AtMostOneAttribute(params string[] parameters)
    {
        Parameters = parameters ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Parameters { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class ExactlyOneAttribute : Attribute
{
    public ExactlyOneAttribute(params string[] parameters)
    {
        Parameters = parameters ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Parameters { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class AtLeastOneAttribute : Attribute
{
    public AtLeastOneAttribute(params string[] parameters)
    {
        Parameters = parameters ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Parameters { get; }
}

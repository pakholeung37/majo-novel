#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

public sealed record ConfigurationIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed class ConfigurationValidationResult
{
    public ConfigurationValidationResult(IEnumerable<ConfigurationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ConfigurationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    public string FormatMessage() => IsValid
        ? "Majo project configuration is valid."
        : string.Join(Environment.NewLine, Issues.Select(issue => issue.ToString()));

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new MajoConfigurationException(Issues);
        }
    }
}

public sealed class MajoConfigurationException : MajoRuntimeException
{
    public MajoConfigurationException(IReadOnlyList<ConfigurationIssue> issues)
        : base("Majo project configuration is invalid:" + Environment.NewLine +
            string.Join(Environment.NewLine, issues.Select(issue => issue.ToString())))
    {
        Issues = issues;
    }

    public IReadOnlyList<ConfigurationIssue> Issues { get; }
}

public static class ValidatableConfigurationExtensions
{
    public static ConfigurationValidationResult Validate(
        this IValidatableConfiguration configuration,
        Func<string, bool>? resourceExists = null,
        MajoProjectConfiguration? root = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigurationIssue>();
        var context = new ConfigurationValidationContext(root, resourceExists, issues);
        configuration.Validate(context);
        return new ConfigurationValidationResult(issues);
    }
}

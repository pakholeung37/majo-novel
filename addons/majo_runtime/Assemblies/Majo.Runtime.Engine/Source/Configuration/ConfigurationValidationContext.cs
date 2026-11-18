#nullable enable

using System;
using System.Collections.Generic;
using Godot;

namespace Majo.Runtime.Configuration;

/// <summary>
/// Provides context and issue accumulation during configuration validation traversal.
/// </summary>
public sealed class ConfigurationValidationContext
{
    public ConfigurationValidationContext(
        MajoProjectConfiguration? root,
        Func<string, bool>? resourceExists = null,
        List<ConfigurationIssue>? issues = null,
        string prefix = "")
    {
        Root = root;
        ResourceExists = resourceExists ?? (path => ResourceLoader.Exists(path));
        Issues = issues ?? new List<ConfigurationIssue>();
        Prefix = prefix ?? string.Empty;
    }

    public MajoProjectConfiguration? Root { get; }

    public Func<string, bool> ResourceExists { get; }

    public List<ConfigurationIssue> Issues { get; }

    public string Prefix { get; }

    public ConfigurationValidationContext ForSection(string sectionName)
    {
        var nextPrefix = string.IsNullOrEmpty(Prefix)
            ? sectionName
            : $"{Prefix}.{sectionName}";

        return new ConfigurationValidationContext(Root, ResourceExists, Issues, nextPrefix);
    }

    public void AddIssue(string propertyPath, string message)
    {
        var fullPath = string.IsNullOrEmpty(Prefix)
            ? propertyPath
            : string.IsNullOrEmpty(propertyPath)
                ? Prefix
                : $"{Prefix}.{propertyPath}";

        Issues.Add(new ConfigurationIssue(fullPath, message));
    }
}

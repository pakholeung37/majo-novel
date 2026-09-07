#nullable enable

using System;

namespace Majo.Runtime.Resources;

/// <summary>A logical backdrop identifier, independent of its configured resource directory.</summary>
public readonly record struct BackdropId
{
    public BackdropId(string value) => Value = new ResourceKey(value).Path;
    public string Value { get; }

    public ResourceKey ToResourceKey(string backgroundsPath)
    {
        var id = new ResourceKey(Value);
        if (string.IsNullOrWhiteSpace(backgroundsPath)) return id;
        var root = new ResourceKey(backgroundsPath).Path;
        return id.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? id : ResourceKey.Combine(root, id.Path);
    }

    public override string ToString() => Value;
}

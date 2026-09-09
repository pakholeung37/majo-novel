#nullable enable

using System;
using System.Linq;

namespace Majo.Runtime.Resources;

public readonly record struct ResourceKey
{
    public ResourceKey(string path)
    {
        Path = Normalize(path);
    }

    public string Path { get; }

    public static ResourceKey Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new ResourceKey(string.Join(
            '/',
            segments.Where(segment => !string.IsNullOrWhiteSpace(segment))));
    }

    public override string ToString() => Path;

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Logical resource keys cannot contain a URI scheme.", nameof(path));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "Logical resource keys cannot be empty or contain traversal segments.",
                nameof(path));
        }

        return string.Join('/', segments);
    }
}

public static class ResourcePath
{
    public static string Combine(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $"{root.Trim().TrimEnd('/').Replace('\\', '/')}/{path.TrimStart('/').Replace('\\', '/')}";
    }

    public static bool IsRooted(string path) =>
        path.Contains("://", StringComparison.Ordinal);
}

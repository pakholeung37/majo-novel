#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Resources;

public enum LocalizedResourceFallbackSource
{
    Exact,
    Parent,
    Default,
    Unlocalized,
    Missing,
}

public sealed record LocalizedResourceResolution(
    ResourceKey LogicalKey,
    string RequestedLocale,
    string? ResolvedLocale,
    ResourceKey? ResolvedKey,
    string? ResolvedOverridePath,
    LocalizedResourceFallbackSource FallbackSource)
{
    public bool IsResolved => FallbackSource != LocalizedResourceFallbackSource.Missing;
}

public sealed record LocalizedPathResolution(
    string LogicalPath,
    string RequestedLocale,
    string? ResolvedLocale,
    string? ResolvedPath,
    LocalizedResourceFallbackSource FallbackSource)
{
    public bool IsResolved => ResolvedPath is not null;
}

public interface ILocalizedResourceResolver : IRuntimeService
{
    LocalizedPathResolution ResolvePath(
        string logicalPath,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        Func<string, bool> exists);

    LocalizedResourceResolution Resolve<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath = null)
        where TResource : class;

    TResource Load<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath,
        out LocalizedResourceResolution resolution)
        where TResource : class;

    Task<(TResource Resource, LocalizedResourceResolution Resolution)> LoadAsync<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class;
}

public sealed class LocalizedResourceResolver : ILocalizedResourceResolver
{
    public const string LocalePlaceholder = "{locale}";
    private readonly IResourceManager resources;

    public LocalizedResourceResolver(IResourceManager resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public LocalizedResourceResolution Resolve<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath = null)
        where TResource : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localizedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLocale);
        ArgumentNullException.ThrowIfNull(fallbackChain);
        var root = new ResourceKey(localizedRoot).Path;
        var suffix = GetSuffix(logicalKey, root);
        var locales = fallbackChain
            .Select(LocaleManager.NormalizeLocale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < locales.Length; index++)
        {
            var locale = locales[index];
            var candidateKey = ResourceKey.Combine(root, locale, suffix);
            var candidateOverride = ContainsLocalePlaceholder(overridePath)
                ? overridePath!.Replace(LocalePlaceholder, locale, StringComparison.Ordinal)
                : null;
            if (!resources.Exists<TResource>(candidateKey, candidateOverride))
            {
                continue;
            }

            return new LocalizedResourceResolution(
                logicalKey,
                requestedLocale,
                locale,
                candidateKey,
                candidateOverride,
                index == 0
                    ? LocalizedResourceFallbackSource.Exact
                    : index == locales.Length - 1
                        ? LocalizedResourceFallbackSource.Default
                        : LocalizedResourceFallbackSource.Parent);
        }

        var flatOverride = ContainsLocalePlaceholder(overridePath)
            ? RemoveLocalePlaceholder(overridePath!)
            : overridePath;
        if (resources.Exists<TResource>(logicalKey, flatOverride))
        {
            return new LocalizedResourceResolution(
                logicalKey,
                requestedLocale,
                null,
                logicalKey,
                flatOverride,
                LocalizedResourceFallbackSource.Unlocalized);
        }

        return new LocalizedResourceResolution(
            logicalKey,
            requestedLocale,
            null,
            null,
            null,
            LocalizedResourceFallbackSource.Missing);
    }

    public LocalizedPathResolution ResolvePath(
        string logicalPath,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLocale);
        ArgumentNullException.ThrowIfNull(fallbackChain);
        ArgumentNullException.ThrowIfNull(exists);
        var normalized = new ResourceKey(logicalPath).Path;
        var locales = fallbackChain
            .Select(LocaleManager.NormalizeLocale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < locales.Length; index++)
        {
            var candidate = ResourceKey.Combine(locales[index], normalized).Path;
            if (!exists(candidate))
            {
                continue;
            }

            return new LocalizedPathResolution(
                normalized,
                requestedLocale,
                locales[index],
                candidate,
                index == 0
                    ? LocalizedResourceFallbackSource.Exact
                    : index == locales.Length - 1
                        ? LocalizedResourceFallbackSource.Default
                        : LocalizedResourceFallbackSource.Parent);
        }

        return exists(normalized)
            ? new LocalizedPathResolution(
                normalized,
                requestedLocale,
                null,
                normalized,
                LocalizedResourceFallbackSource.Unlocalized)
            : new LocalizedPathResolution(
                normalized,
                requestedLocale,
                null,
                null,
                LocalizedResourceFallbackSource.Missing);
    }

    public TResource Load<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath,
        out LocalizedResourceResolution resolution)
        where TResource : class
    {
        resolution = Resolve<TResource>(
            logicalKey, localizedRoot, requestedLocale, fallbackChain, overridePath);
        if (resolution.ResolvedKey is not { } resolvedKey)
        {
            throw new ResourceNotFoundException(logicalKey, typeof(TResource), []);
        }

        return resources.Load<TResource>(resolvedKey, resolution.ResolvedOverridePath);
    }

    public async Task<(TResource Resource, LocalizedResourceResolution Resolution)> LoadAsync<TResource>(
        ResourceKey logicalKey,
        string localizedRoot,
        string requestedLocale,
        IReadOnlyList<string> fallbackChain,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class
    {
        var resolution = Resolve<TResource>(
            logicalKey, localizedRoot, requestedLocale, fallbackChain, overridePath);
        if (resolution.ResolvedKey is not { } resolvedKey)
        {
            throw new ResourceNotFoundException(logicalKey, typeof(TResource), []);
        }

        var resource = await resources.LoadAsync<TResource>(
            resolvedKey,
            resolution.ResolvedOverridePath,
            cancellationToken);
        return (resource, resolution);
    }

    private static string GetSuffix(ResourceKey key, string root)
    {
        if (!key.Path.StartsWith($"{root}/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Logical key '{key.Path}' is not under localized root '{root}'.",
                nameof(key));
        }

        return key.Path[(root.Length + 1)..];
    }

    private static bool ContainsLocalePlaceholder(string? path) =>
        path?.Contains(LocalePlaceholder, StringComparison.Ordinal) == true;

    private static string RemoveLocalePlaceholder(string path) =>
        path.Replace($"{LocalePlaceholder}/", string.Empty, StringComparison.Ordinal)
            .Replace($"/{LocalePlaceholder}", string.Empty, StringComparison.Ordinal)
            .Replace(LocalePlaceholder, string.Empty, StringComparison.Ordinal);
}

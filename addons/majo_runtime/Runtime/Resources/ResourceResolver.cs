#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

internal sealed class ResourceResolver
{
    private readonly IResourceProvider[] providers;
    private readonly IRuntimeLogger logger;
    public ResourceResolver(IResourceProvider[] providers, IRuntimeLogger logger)
    {
        ValidateProviders(providers);
        this.providers = providers;
        this.logger = logger;
    }

    public ResourcePreloadEntry ResolvePreloadEntry(
        ResourcePreloadEntry entry,
        ResourcePreloadContext context)
    {
        entry.Validate();
        if (entry.LocaleChannel == ResourceLocaleChannel.None) return entry;
        var requestedLocale = entry.LocaleChannel == ResourceLocaleChannel.Dialogue
            ? context.DialogueLocale : context.VoiceLocale;
        var fallback = entry.LocaleChannel == ResourceLocaleChannel.Dialogue
            ? context.DialogueFallbackChain : context.VoiceFallbackChain;
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLocale);
        var root = new ResourceKey(entry.LocalizedRoot!).Path;
        if (!entry.Key.Path.StartsWith($"{root}/", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Logical key '{entry.Key.Path}' is not under localized root '{root}'.");
        var suffix = entry.Key.Path[(root.Length + 1)..];
        foreach (var locale in fallback.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = ResourceKey.Combine(root, locale, suffix);
            var candidateOverride = entry.OverridePath?.Contains(
                    LocalizedResourceResolver.LocalePlaceholder, StringComparison.Ordinal) == true
                ? entry.OverridePath.Replace(
                    LocalizedResourceResolver.LocalePlaceholder, locale, StringComparison.Ordinal)
                : null;
            if (Resolve(candidate, entry.ResourceType, candidateOverride, false).Location is not null)
            {
                logger.Debug("resource", "resource.variant_resolved",
                    $"Resource '{entry.Key.Path}' for locale '{requestedLocale}' resolved to '{candidate.Path}'.");
                return entry with { Key = candidate, OverridePath = candidateOverride };
            }
        }
        var flatOverride = RemoveLocalePlaceholder(entry.OverridePath);
        if (Resolve(entry.Key, entry.ResourceType, flatOverride, false).Location is not null)
            return entry with { OverridePath = flatOverride };
        throw CreateNotFound(entry.Key, entry.ResourceType, entry.OverridePath);
    }

    public ResolvedResource Resolve(
        ResourceKey key,
        Type resourceType,
        string? overridePath,
        bool throwIfMissing = true)
    {
        var request = new ResourceRequest(key, resourceType, overridePath);
        var candidates = new List<string>();
        foreach (var provider in providers)
        {
            candidates.AddRange(provider.GetCandidatePaths(request));
            if (provider.TryResolve(request, out var location))
                return new ResolvedResource(provider, location,
                    candidates.Distinct(StringComparer.Ordinal).ToArray());
        }
        var distinct = candidates.Distinct(StringComparer.Ordinal).ToArray();
        if (throwIfMissing)
        {
            logger.Warning("resource", "resource.resolve_failed",
                $"Resource '{key.Path}' of type '{resourceType.Name}' could not be resolved.");
            throw new ResourceNotFoundException(key, resourceType, distinct);
        }
        return new ResolvedResource(providers[0], null, distinct);
    }

    private ResourceNotFoundException CreateNotFound(ResourceKey key, Type type, string? overridePath)
    {
        var resolved = Resolve(key, type, overridePath, false);
        return new ResourceNotFoundException(key, type, resolved.Candidates);
    }

    private static string? RemoveLocalePlaceholder(string? path) => path?
        .Replace($"{LocalizedResourceResolver.LocalePlaceholder}/", string.Empty, StringComparison.Ordinal)
        .Replace($"/{LocalizedResourceResolver.LocalePlaceholder}", string.Empty, StringComparison.Ordinal)
        .Replace(LocalizedResourceResolver.LocalePlaceholder, string.Empty, StringComparison.Ordinal);

    private static void ValidateProviders(IResourceProvider[] providers)
    {
        if (providers.Length == 0) throw new MajoResourceException("At least one resource provider is required.");
        if (providers.Any(provider => provider is null))
            throw new ArgumentException("Resource providers cannot contain null.", nameof(providers));
        if (providers.FirstOrDefault(provider => string.IsNullOrWhiteSpace(provider.Id)) is not null)
            throw new MajoResourceException("Every resource provider requires an ID.");
        var duplicate = providers.GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new DuplicateResourceProviderException(duplicate.Key);
    }
}

internal sealed record ResolvedResource(
    IResourceProvider Provider, ResourceLocation? Location, IReadOnlyList<string> Candidates);

#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Resources;

public class MajoResourceException : Exception
{
    public MajoResourceException(string message)
        : base(message)
    {
    }

    public MajoResourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ResourceNotFoundException : MajoResourceException
{
    public ResourceNotFoundException(
        ResourceKey key,
        Type resourceType,
        IReadOnlyCollection<string> candidates)
        : base(CreateMessage(key, resourceType, candidates))
    {
        Key = key;
        ResourceType = resourceType;
        Candidates = candidates;
    }

    public ResourceKey Key { get; }

    public Type ResourceType { get; }

    public IReadOnlyCollection<string> Candidates { get; }

    private static string CreateMessage(
        ResourceKey key,
        Type resourceType,
        IReadOnlyCollection<string> candidates) =>
        $"Resource '{key}' of type '{resourceType.FullName}' was not found. " +
        $"Candidates: {(candidates.Count == 0 ? "<none>" : string.Join(", ", candidates))}.";
}

public sealed class ResourceTypeMismatchException : MajoResourceException
{
    public ResourceTypeMismatchException(
        string resolvedPath,
        Type expectedType,
        Type actualType)
        : base(
            $"Resource '{resolvedPath}' is '{actualType.FullName}', " +
            $"not '{expectedType.FullName}'.")
    {
    }
}

public sealed class DuplicateResourceProviderException : MajoResourceException
{
    public DuplicateResourceProviderException(string providerId)
        : base($"Resource provider '{providerId}' is already registered.")
    {
    }
}

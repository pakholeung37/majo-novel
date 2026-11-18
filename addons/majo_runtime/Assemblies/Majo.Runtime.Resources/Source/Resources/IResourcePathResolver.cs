#nullable enable

using System.Collections.Generic;

namespace Majo.Runtime.Resources;

/// <summary>
/// Converts a logical resource request into one or more provider-specific paths.
/// </summary>
public interface IResourcePathResolver
{
    IReadOnlyList<string> GetCandidatePaths(ResourceRequest request);
}

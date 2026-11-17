#nullable enable

using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

/// <summary>A resource intent, resolved by the preloader against the current project configuration.
/// Kinds are character, object, backdrop, bgm, sfx and voice. Actor IDs and asset IDs remain distinct.</summary>
public sealed record CommandResourceReference(
    string Kind,
    string Id,
    string? Asset = null,
    string? Appearance = null,
    string? Stage = null);

/// <summary>Opt-in resource description for bound command arguments. Implementations, converters and
/// validators used here must be side-effect free: look-ahead never executes the command.</summary>
public interface ICommandResourceProvider<in TArguments> where TArguments : class
{
    IReadOnlyList<CommandResourceReference> DescribeResources(TArguments arguments);
}

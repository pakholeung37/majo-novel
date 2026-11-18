#nullable enable

namespace Majo.Runtime.Direction.Core;

public sealed record ResolvedBackdropAppearance(string FactoryId, string Appearance);

/// <summary>Read-only definition metadata for planning; implementations must not create actors or load presentation resources.</summary>
public interface IBackdropAppearanceResolver
{
    string ResolveFactoryId(string definitionId);

    ResolvedBackdropAppearance ResolveAppearance(
        string definitionId, string? appearance, string? previousAppearance);
}

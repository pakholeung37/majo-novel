#nullable enable

namespace Majo.Runtime.Configuration;

/// <summary>
/// Defines a configuration contract that can validate its own properties and consistency.
/// </summary>
public interface IValidatableConfiguration
{
    void Validate(ConfigurationValidationContext context);
}

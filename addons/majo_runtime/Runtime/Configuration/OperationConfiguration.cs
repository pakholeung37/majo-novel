using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class OperationConfiguration : Resource, IValidatableConfiguration
{
    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}

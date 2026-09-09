using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class InputConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public bool InstallDefaultBindings { get; set; } = true;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}

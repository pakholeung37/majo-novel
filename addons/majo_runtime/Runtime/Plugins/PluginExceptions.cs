#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Plugins;

public sealed class PluginDependencyException : MajoRuntimeException
{
    public PluginDependencyException(string message) : base(message) { }
}

public class PluginRegistrationException : InvalidOperationException
{
    public PluginRegistrationException(string message) : base(message) { }
}

public sealed class DuplicatePluginException : PluginRegistrationException
{
    public DuplicatePluginException(string pluginId)
        : base($"Plugin with ID '{pluginId}' is already registered.") { }
}

public sealed class PluginRegistrationSealedException : PluginRegistrationException
{
    public PluginRegistrationSealedException()
        : base("Plugins must be registered before Runtime initialization.") { }
}

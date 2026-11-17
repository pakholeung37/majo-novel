#pragma warning disable CA2255 // Addon-provided application module initialization.

using System.Runtime.CompilerServices;

namespace YarnSpinnerGodot.Generated;

// The upstream generator adds this class's static constructor for game-owned Yarn actions.
// Trigger it in the game assembly as well as the upstream library's own registration.
public partial class ActionRegistration
{
    [ModuleInitializer]
    public static void Touch() { }
}

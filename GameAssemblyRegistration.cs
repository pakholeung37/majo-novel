#pragma warning disable CA2255 // Game-owned libraries register before scene loading.

using System.Runtime.CompilerServices;
using Majo.Runtime.Integration;

internal static class GameAssemblyRegistration
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        GodotScriptAssemblies.Register(typeof(Majo.Demo.DemoSessionService).Assembly);
#if TOOLS
        GodotScriptAssemblies.Register(typeof(Majo.Runtime.HeadlessTests.HeadlessTestRunner).Assembly);
#endif
    }
}

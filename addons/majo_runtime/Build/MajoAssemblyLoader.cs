using Godot;
using Majo.Runtime.Integration;

namespace Majo.Runtime.Build;

[Tool]
internal sealed partial class MajoAssemblyLoader : Node
{
    // Packed scenes can resolve scripts before autoloads enter the tree.
    public MajoAssemblyLoader() => GodotScriptAssemblies.Initialize();

    public override void _EnterTree() => GodotScriptAssemblies.Initialize();
}

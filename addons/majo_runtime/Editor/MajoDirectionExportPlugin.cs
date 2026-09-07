#if TOOLS
using System;
using Godot;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.Editor;

[Tool]
public partial class MajoDirectionExportPlugin : EditorExportPlugin
{
    public override string _GetName() => "MajoDirection";

    public override void _ExportFile(string path, string type, string[] features)
    {
        if (!path.EndsWith(".yarnproject", StringComparison.OrdinalIgnoreCase))
            return;
        MajoDirectionCompiler.Compile(path);
        var assetPath = MajoDirectionData.GetPath(path);
        if (!FileAccess.FileExists(assetPath))
        {
            GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Error, "Majo Direction",
                $"Cannot export Yarn project without Direction asset: '{assetPath}'.");
            return;
        }
        // Explicit inclusion also supports exporting selected scenes/resources.
        AddFile(assetPath, FileAccess.GetFileAsBytes(assetPath), false);
        const string script = "res://addons/majo_runtime/Runtime/Dialogue/Yarn/MajoDirectionData.cs";
        AddFile(script, FileAccess.GetFileAsBytes(script), false);
    }
}
#endif

#if TOOLS
using System;
using Godot;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.Editor;

[Tool]
public partial class MajoDirectionExportPlugin : EditorExportPlugin
{
    public override string _GetName() => "MajoDirection";

    public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
    {
        // These assets are loaded by runtime paths, so scene dependency traversal cannot find them.
        IncludeRuntimeDirectory("res://addons/majo_runtime/Runtime");
        IncludeRuntimeDirectory("res://addons/majo_runtime/Build");
        IncludeRuntimeDirectory("res://addons/YarnSpinner-Godot/Runtime");
        const string optionScene = "res://addons/YarnSpinner-Godot/Scenes/OptionItem.tscn";
        AddFile(optionScene, FileAccess.GetFileAsBytes(optionScene), false);
    }

    private void IncludeRuntimeDirectory(string path)
    {
        using var directory = DirAccess.Open(path);
        if (directory is null) return;
        foreach (var file in directory.GetFiles())
        {
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) ||
                  file.EndsWith(".tscn", StringComparison.Ordinal) ||
                  file.EndsWith(".gdshader", StringComparison.Ordinal) || file.EndsWith(".gd", StringComparison.Ordinal))) continue;
            var resourcePath = path + "/" + file;
            AddFile(resourcePath, FileAccess.GetFileAsBytes(resourcePath), false);
        }
        foreach (var child in directory.GetDirectories())
            IncludeRuntimeDirectory(path + "/" + child);
    }

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

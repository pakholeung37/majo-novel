#if TOOLS
using System;
using Godot;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Editor;

namespace Majo.Runtime;

[Tool]
public partial class MajoRuntimePlugin : EditorPlugin
{
    private EditorFileSystem filesystem;
    private MajoDirectionExportPlugin directionExport;
    private bool rebuildQueued;

    public override void _EnterTree()
    {
        MajoDirectionCompiler.InstallCompilationPipeline();
        filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        filesystem.FilesystemChanged += QueueRebuild;
        filesystem.ResourcesReimported += OnResourcesReimported;
        directionExport = new MajoDirectionExportPlugin();
        AddExportPlugin(directionExport);
        QueueRebuild();
        var status = YarnDependencyValidator.ValidateProject(
            ProjectSettings.GlobalizePath("res://"));

        if (!status.IsCompatible)
        {
            GD.PushError($"Majo Runtime dependency check failed: {status.Message}");
        }
    }

    public override void _ExitTree()
    {
        YarnSpinnerGodot.YarnProjectEditorUtility.CompilationPipeline = null;
        filesystem.FilesystemChanged -= QueueRebuild;
        filesystem.ResourcesReimported -= OnResourcesReimported;
        RemoveExportPlugin(directionExport);
        directionExport.Dispose();
    }

    private void OnResourcesReimported(string[] paths) => QueueRebuild();

    private void QueueRebuild()
    {
        if (rebuildQueued)
            return;
        rebuildQueued = true;
        Callable.From(RebuildDirections).CallDeferred();
    }

    private void RebuildDirections()
    {
        rebuildQueued = false;
        if (!IsInsideTree() || filesystem.IsScanning())
            return;
        RebuildDirectory(filesystem.GetFilesystem());
    }

    private void RebuildDirectory(EditorFileSystemDirectory directory)
    {
        for (var index = 0; index < directory.GetFileCount(); index++)
        {
            var path = directory.GetFilePath(index);
            const string suffix = ".direction.tres";
            if (path.EndsWith(".yarnproject" + suffix, StringComparison.OrdinalIgnoreCase) &&
                !FileAccess.FileExists(path[..^suffix.Length]))
            {
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
                filesystem.UpdateFile(path);
                continue;
            }
            if (path.EndsWith(".yarnproject", StringComparison.OrdinalIgnoreCase) &&
                MajoDirectionCompiler.Compile(path))
                filesystem.UpdateFile(MajoDirectionData.GetPath(path));
        }
        for (var index = 0; index < directory.GetSubdirCount(); index++)
            RebuildDirectory(directory.GetSubdir(index));
    }
}
#endif

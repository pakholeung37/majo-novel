#nullable enable

using System;
using System.Linq;
using Godot;
using Majo.Runtime.Dialogue.Yarn;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private static void ValidateImportedDirectionData()
    {
        var imported = ResourceLoader.Load<YarnProject>("res://demos/demo-01/assets/scripts/main.yarnproject");
        var modules = MajoDirectionData.LoadModules(imported);
        Assert(modules.Count > 0, "Imported Direction modules were unavailable without hot reload.");
        Assert(modules.All(module => imported.Program.Nodes.ContainsKey(module.NodeId)),
            "Direction companion does not match the imported Yarn nodes.");
        Assert(typeof(YarnProject).GetProperty("CompiledDirectionModulesJson") is null,
            "Majo still injects serialized properties into YarnProject.");

        const string manifest = "user://direction-data-test.yarnproject";
        var path = MajoDirectionData.GetPath(manifest);
        using var project = new YarnProject
        {
            JSONProjectPath = manifest,
            CompiledYarnProgramBase64 = imported.CompiledYarnProgramBase64,
        };
        using var data = new MajoDirectionData
        {
            ManifestPath = manifest,
            ProgramFingerprint = MajoDirectionData.HashProgram(project),
            ModulesJson = YarnProjectDirectionMetadata.Serialize(modules),
        };
        try
        {
            Assert(ResourceSaver.Save(data, path) == Error.Ok, "Could not save Direction test companion.");
            Assert(MajoDirectionData.LoadModules(project).Count == modules.Count,
                "Independent Direction resource did not survive disk round trip.");
            data.SchemaVersion++;
            Assert(ResourceSaver.Save(data, path) == Error.Ok, "Could not save incompatible schema fixture.");
            ExpectDirectionLoadFailure(project);
            data.SchemaVersion = MajoDirectionData.CurrentSchemaVersion;
            data.ProgramFingerprint = "stale";
            Assert(ResourceSaver.Save(data, path) == Error.Ok, "Could not save stale program fixture.");
            ExpectDirectionLoadFailure(project);
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            ExpectDirectionLoadFailure(project);
        }
        finally
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }

    private static void ExpectDirectionLoadFailure(YarnProject project)
    {
        try
        {
            MajoDirectionData.LoadModules(project);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Missing or stale Direction data was silently accepted.");
    }
}

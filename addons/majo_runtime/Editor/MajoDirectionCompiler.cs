#if TOOLS
#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using YarnSpinnerGodot;
using File = System.IO.File;

namespace Majo.Runtime.Editor;

/// <summary>Owns Direction compilation without modifying the Yarn importer or resource type.</summary>
public static class MajoDirectionCompiler
{
    public static void InstallCompilationPipeline() =>
        YarnProjectEditorUtility.CompilationPipeline = CompileProgram;

    private static global::Yarn.Compiler.CompilationResult CompileProgram(global::Yarn.Compiler.CompilationJob job)
    {
        var transform = new YarnNodeEntryTransform();
        job.Inputs = job.Inputs.Select(input => input is global::Yarn.Compiler.CompilationJob.File file
            ? (global::Yarn.Compiler.ISourceInput)transform.Transform(file.FileName, file.Source) : input).ToArray();
        var result = global::Yarn.Compiler.Compiler.Compile(job);
        transform.RestoreSourceCoordinates(result);
        return result;
    }

    public static bool Compile(string manifestPath)
    {
        InstallCompilationPipeline();
        var assetPath = MajoDirectionData.GetPath(manifestPath);
        try
        {
            var absoluteManifest = ProjectSettings.GlobalizePath(manifestPath);
            var manifestText = File.ReadAllText(absoluteManifest);
            var manifest = global::Yarn.Compiler.Project.LoadFromFile(absoluteManifest);
            var files = manifest.SourceFiles.OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                var content = File.ReadAllText(path);
                return new YarnSourceFileSnapshot(ProjectSettings.LocalizePath(path), path, content, Hash(content));
            }).ToArray();
            var fingerprint = Hash(manifestText + "\n" +
                string.Join("\n", files.Select(file => file.LogicalPath + ":" + file.ContentHash)));
            var project = ResourceLoader.Load<YarnProject>(manifestPath, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (project is null)
                throw new InvalidOperationException("Yarn project has not compiled successfully.");
            var programHash = string.IsNullOrEmpty(project.CompiledYarnProgramBase64)
                ? string.Empty : MajoDirectionData.HashProgram(project);
            if (ResourceLoader.Exists(assetPath))
            {
                using var existing = ResourceLoader.Load<MajoDirectionData>(assetPath, cacheMode: ResourceLoader.CacheMode.Ignore);
                if (existing is not null && existing.SchemaVersion == MajoDirectionData.CurrentSchemaVersion &&
                    existing.ManifestPath == manifestPath && existing.SourceFingerprint == fingerprint &&
                    existing.ProgramFingerprint == programHash && project.ProjectErrors.Length == 0)
                    return false;
            }

            // Orchestrate the public Yarn compiler from Majo so source deletions and
            // manifest edits cannot pair new Direction data with old Yarn bytecode.
            if (files.Length == 0)
                throw new InvalidOperationException("Yarn project contains no source files.");
            YarnProjectEditorUtility.UpdateYarnProjectImmediate(project);
            if (project.ProjectErrors.Length > 0 || string.IsNullOrEmpty(project.CompiledYarnProgramBase64))
                throw new InvalidOperationException("Yarn project has not compiled successfully.");
            if (manifestText != File.ReadAllText(absoluteManifest) ||
                !manifest.SourceFiles.OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(files.Select(file => file.AbsolutePath)) ||
                files.Any(file => File.ReadAllText(file.AbsolutePath) != file.Content))
                throw new IOException("Yarn sources changed during compilation; save again to retry.");
            programHash = MajoDirectionData.HashProgram(project);

            var snapshot = new YarnSourceSnapshot(Path.GetFileNameWithoutExtension(manifestPath), manifestPath,
                manifest.FileVersion, manifest.BaseLanguage, files, fingerprint);
            var modules = NodeDirectionCompiler.Compile(snapshot);
            var errors = modules.SelectMany(module => module.Diagnostics)
                .Where(diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (errors.Length > 0)
                throw new InvalidOperationException(string.Join("\n", errors.Select(error =>
                    $"{error.File}:{error.StartLine}: {error.Message}")));

            using var data = new MajoDirectionData
            {
                SchemaVersion = MajoDirectionData.CurrentSchemaVersion,
                ManifestPath = manifestPath,
                SourceFingerprint = fingerprint,
                ProgramFingerprint = programHash,
                ModulesJson = YarnProjectDirectionMetadata.Serialize(modules),
            };
            var result = ResourceSaver.Save(data, assetPath);
            if (result != Error.Ok)
                throw new IOException($"Saving '{assetPath}' failed: {result}.");
            return true;
        }
        catch (Exception exception)
        {
            // A failed rebuild must not leave apparently valid old Direction data behind.
            var absolutePath = ProjectSettings.GlobalizePath(assetPath);
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);
            GD.PushError($"Direction compilation failed for '{manifestPath}': {exception.Message}");
            return false;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
#endif

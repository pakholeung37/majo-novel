#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yarn;
using Yarn.Compiler;
using YarnSpinnerGodot;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed record YarnRuntimeCompilation(
    YarnSourceSnapshot Snapshot,
    CompilationResult Result,
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics)
{
    public IReadOnlyList<NodeDirectionModule> DirectionModules { get; init; } =
        Array.Empty<NodeDirectionModule>();

    public bool HasErrors => Diagnostics.Any(diagnostic =>
        string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase));
}

public sealed class YarnRuntimeCompiler
{
    private readonly Library library;

    public YarnRuntimeCompiler(Library? library = null)
    {
        this.library = library ?? Actions.GetLibrary();
    }

    public Task<YarnRuntimeCompilation> CompileAsync(
        YarnSourceSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => CompileAsync(snapshot, 1, cancellationToken);

    public Task<YarnRuntimeCompilation> CompileAsync(
        YarnSourceSnapshot snapshot,
        long directionVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.Run(() => Compile(snapshot, directionVersion, cancellationToken), cancellationToken);
    }

    private YarnRuntimeCompilation Compile(
        YarnSourceSnapshot snapshot,
        long directionVersion,
        CancellationToken cancellationToken)
    {
        var transform = new YarnNodeEntryTransform();
        var files = snapshot.Files.Select(file => (ISourceInput)transform.Transform(file.LogicalPath, file.Content)).ToArray();
        var job = CompilationJob.CreateFromInputs(
            files,
            library,
            snapshot.LanguageVersion);
        job.CompilationType = CompilationJob.Type.FullCompilation;
        job.CancellationToken = cancellationToken;
        var result = global::Yarn.Compiler.Compiler.Compile(job);
        transform.RestoreSourceCoordinates(result);
        var diagnostics = result.Diagnostics.Select(diagnostic =>
        {
            var file = string.IsNullOrWhiteSpace(diagnostic.FileName)
                ? snapshot.ManifestPath
                : diagnostic.FileName.Replace('\\', '/');
            return new YarnCompilationDiagnostic(
                diagnostic.Severity.ToString(),
                diagnostic.Code,
                file,
                diagnostic.Range.Start.Line,
                diagnostic.Range.Start.Character,
                diagnostic.Range.End.Line,
                diagnostic.Range.End.Character,
                diagnostic.Message);
        }).ToArray();
        var directionModules = NodeDirectionCompiler.Compile(snapshot, directionVersion);
        var directionDiagnostics = directionModules
            .SelectMany(module => module.Diagnostics)
            .ToArray();
        var allDiagnostics = diagnostics.Concat(directionDiagnostics).ToArray();
        return new YarnRuntimeCompilation(snapshot, result, Array.AsReadOnly(allDiagnostics))
        {
            DirectionModules = directionModules,
        };
    }
}

using System;
using System.IO;
using System.Linq;
using Xunit;
using Yarn.Compiler;

namespace Majo.Runtime.UnitTests;

public sealed class ManosabaScriptCompilationTests
{
    [Fact]
    public void ManosabaYarnFilesCompileSuccessfully()
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../demos/manosaba/assets/scripts"));
        var yarnPaths = Directory.GetFiles(
            basePath,
            "*.yarn",
            SearchOption.AllDirectories);

        Assert.NotEmpty(yarnPaths);

        var job = CompilationJob.CreateFromFiles(yarnPaths);
        var result = Compiler.Compile(job);

        var errors = result.Diagnostics
            .Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.Empty(errors);
        Assert.NotNull(result.Program);
        Assert.True(result.Program.Nodes.ContainsKey("Start"));
        Assert.True(result.Program.Nodes.ContainsKey("Act01_Chapter01_Adv01"));
        Assert.True(result.Program.Nodes.ContainsKey("Act01_Chapter01_Adv02"));
        Assert.True(result.Program.Nodes.ContainsKey("EndAdv"));
    }
}

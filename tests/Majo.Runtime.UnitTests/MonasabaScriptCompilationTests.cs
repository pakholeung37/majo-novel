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
        var mainYarnPath = Path.Combine(basePath, "main.yarn");
        var adv01YarnPath = Path.Combine(basePath, "Act01_Chapter01/Act01_Chapter01_Adv01.yarn");

        Assert.True(File.Exists(mainYarnPath), $"File not found: {mainYarnPath}");
        Assert.True(File.Exists(adv01YarnPath), $"File not found: {adv01YarnPath}");

        var job = CompilationJob.CreateFromFiles(new[] { mainYarnPath, adv01YarnPath });
        var result = Compiler.Compile(job);

        var errors = result.Diagnostics
            .Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.Empty(errors);
        Assert.NotNull(result.Program);
        Assert.True(result.Program.Nodes.ContainsKey("Start"));
        Assert.True(result.Program.Nodes.ContainsKey("Act01_Chapter01_Adv01"));
    }
}

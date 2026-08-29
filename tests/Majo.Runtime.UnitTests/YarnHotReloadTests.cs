#nullable enable

using Google.Protobuf;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Xunit;


namespace Majo.Runtime.UnitTests;

public sealed class YarnHotReloadTests
{
    [Fact]
    public async Task SnapshotExpandsGlobExcludesFilesAndIsDeterministic()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("main.yarnproject", """
            {
              "projectFileVersion": 3,
              "sourceFiles": ["**/*.yarn"],
              "excludeFiles": ["ignored/**"],
              "baseLanguage": "en"
            }
            """);
        directory.Write("chapter/b.yarn", Yarn("B", "line:b", "Second"));
        directory.Write("a.yarn", Yarn("A", "line:a", "First"));
        directory.Write("ignored/c.yarn", Yarn("C", "line:c", "Ignored"));

        var reader = new YarnSourceSnapshotReader();
        var first = await reader.ReadAsync("main", directory.PathOf("main.yarnproject"));
        var second = await reader.ReadAsync("main", directory.PathOf("main.yarnproject"));

        Assert.Equal(new[] { "a.yarn", "chapter/b.yarn" },
            first.Files.Select(file => file.LogicalPath));
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal("en", first.BaseLanguage);
    }

    [Fact]
    public async Task SnapshotFingerprintChangesWhenSourceChanges()
    {
        using var directory = CreateProject(Yarn("Start", "line:start", "Version one"));
        var reader = new YarnSourceSnapshotReader();
        var first = await reader.ReadAsync("main", directory.PathOf("main.yarnproject"));

        directory.Write("main.yarn", Yarn("Start", "line:start", "Version two"));
        var second = await reader.ReadAsync("main", directory.PathOf("main.yarnproject"));

        Assert.NotEqual(first.SourceFingerprint, second.SourceFingerprint);
    }

    [Fact]
    public async Task CompilerRejectsInvalidSourceWithoutProgram()
    {
        using var directory = CreateProject("title: Start\n---\n<<if true>>\nBroken\n===\n");
        var snapshot = await new YarnSourceSnapshotReader().ReadAsync(
            "main", directory.PathOf("main.yarnproject"));

        var compilation = await new YarnRuntimeCompiler().CompileAsync(snapshot);

        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Severity == "Error");
    }

    [Fact]
    public async Task CompilerUsesInMemorySnapshotAndProducesStableLineMetadata()
    {
        using var directory = CreateProject(Yarn("Start", "line:stable", "Hello"));
        var snapshot = await new YarnSourceSnapshotReader().ReadAsync(
            "main", directory.PathOf("main.yarnproject"));

        // Prove compilation consumes the captured content rather than reading this file again.
        directory.Write("main.yarn", "not valid yarn");
        var compilation = await new YarnRuntimeCompiler().CompileAsync(snapshot);

        Assert.False(compilation.HasErrors);
        Assert.NotNull(compilation.Result.Program);
        Assert.Contains("line:stable", compilation.Result.StringTable!.Keys);
        Assert.False(compilation.Result.StringTable["line:stable"].isImplicitTag);
    }

    [Fact]
    public async Task CompilerProducesIdenticalProgramAndStringsWhenOnlyCommentsChange()
    {
        using var directory = CreateProject(Yarn("Start", "line:1", "Hello"));
        var snapshot = await new YarnSourceSnapshotReader().ReadAsync(
            "main", directory.PathOf("main.yarnproject"));

        var compilation1 = await new YarnRuntimeCompiler().CompileAsync(snapshot);

        // Add a comment line (source fingerprint changes, but compiled program and strings are identical)
        directory.Write("main.yarn", "// just a comment\n" + Yarn("Start", "line:1", "Hello"));
        var snapshot2 = await new YarnSourceSnapshotReader().ReadAsync(
            "main", directory.PathOf("main.yarnproject"));
        var compilation2 = await new YarnRuntimeCompiler().CompileAsync(snapshot2);

        Assert.NotEqual(snapshot.SourceFingerprint, snapshot2.SourceFingerprint);
        Assert.NotNull(compilation1.Result.Program);
        Assert.NotNull(compilation2.Result.Program);
        Assert.True(Google.Protobuf.ByteString.CopyFrom(compilation1.Result.Program.ToByteArray())
            .Equals(Google.Protobuf.ByteString.CopyFrom(compilation2.Result.Program.ToByteArray())));
        Assert.Equal(compilation1.Result.StringTable!["line:1"].text, compilation2.Result.StringTable!["line:1"].text);
    }



    private static TemporaryDirectory CreateProject(string source)
    {
        var directory = new TemporaryDirectory();
        directory.Write("main.yarnproject", """
            {
              "projectFileVersion": 3,
              "sourceFiles": ["main.yarn"],
              "excludeFiles": [],
              "baseLanguage": "en"
            }
            """);
        directory.Write("main.yarn", source);
        return directory;
    }

    private static string Yarn(string node, string lineId, string text) => $$"""
        title: {{node}}
        ---
        {{text}} #{{lineId}}
        ===

        """;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "majo-hot-reload-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string PathOf(string relativePath) => System.IO.Path.Combine(Root, relativePath);

        public void Write(string relativePath, string content)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

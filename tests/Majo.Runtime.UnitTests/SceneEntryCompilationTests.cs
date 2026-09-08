using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Yarn;
using Yarn.Compiler;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SceneEntryCompilationTests
{
    [Fact]
    public void MetadataRoundTripsAndOrdinaryNodesHaveNoScene()
    {
        var modules = NodeDirectionCompiler.Compile(Snapshot("title: Start\nscene: room\n---\nHello #line:a\n===\ntitle: Choice\n---\nBye #line:b\n==="));
        Assert.Equal("room", modules[0].SceneEntry?.SceneId);
        Assert.Equal(2, modules[0].SceneEntry?.Line);
        Assert.Null(modules[1].SceneEntry);
        var restored = YarnProjectDirectionMetadata.Deserialize(YarnProjectDirectionMetadata.Serialize(modules));
        Assert.Equal(modules[0].SceneEntry, restored[0].SceneEntry);
    }

    [Theory]
    [InlineData("scene: \n")]
    [InlineData("scene: a\nscene: b\n")]
    public void InvalidHeadersAreDiagnosed(string header)
    {
        var modules = NodeDirectionCompiler.Compile(Snapshot($"title: Start\n{header}---\nHello #line:a\n==="));
        Assert.Contains(modules[0].Diagnostics, value => value.Code == "MAJO_SCENE");
    }

    [Fact]
    public void DuplicateSceneEntriesAndReservedCommandsAreDiagnosed()
    {
        var modules = NodeDirectionCompiler.Compile(Snapshot($"title: A\nscene: room\n---\n<<{YarnNodeEntryTransform.CommandName}>>\n===\ntitle: B\nscene: room\n---\nBye #line:b\n==="));
        Assert.Contains(modules[0].Diagnostics, value => value.Code == "MAJO_RESERVED_COMMAND");
        Assert.Contains(modules[1].Diagnostics, value => value.Code == "MAJO_SCENE");
    }

    [Theory]
    [InlineData("<<set $entered = true>>\nHello #line:a")]
    [InlineData("<<jump Next>>")]
    [InlineData("Hello #line:a")]
    [InlineData("-> First #line:a\n    Hello #line:b")]
    public void PreludeStopsVmBeforeAnyAuthoredInstruction(string body)
    {
        var transform = new YarnNodeEntryTransform();
        var input = transform.Transform("test.yarn", $"title: Start\nscene: room\n---\n{body}\n===\ntitle: Next\n---\nNext #line:next\n===");
        var result = Compiler.Compile(CompilationJob.CreateFromInputs(new[] { input }));
        Assert.False(result.ContainsErrors, string.Join("\n", result.Diagnostics));
        transform.RestoreSourceCoordinates(result);
        var storage = new MemoryVariableStore();
        var dialogue = new global::Yarn.Dialogue(storage);
        var commands = new List<string>();
        var lines = 0;
        var options = 0;
        dialogue.CommandHandler = command => commands.Add(command.Text);
        dialogue.LineHandler = _ => lines++;
        dialogue.OptionsHandler = _ => options++;
        dialogue.SetProgram(result.Program!);
        dialogue.SetNode("Start");
        dialogue.Continue();
        Assert.Equal(new[] { YarnNodeEntryTransform.CommandName }, commands);
        Assert.Equal(0, lines);
        Assert.Equal(0, options);
        Assert.False(storage.TryGetValue<bool>("$entered", out var entered) && entered);
        Assert.Equal("Start", dialogue.CurrentNode);
        dialogue.Continue();
        Assert.True(lines > 0 || options > 0 || commands.Count > 1);
    }

    [Fact]
    public void TransformPreservesAuthoredLineAndDiagnosticCoordinates()
    {
        var transform = new YarnNodeEntryTransform();
        var input = transform.Transform("test.yarn", "title: A\n---\nHello #line:a\n===\ntitle: B\n---\nBye #line:b\n===");
        var result = Compiler.Compile(CompilationJob.CreateFromInputs(new[] { input }));
        transform.RestoreSourceCoordinates(result);
        Assert.Equal(3, result.StringTable!["line:a"].lineNumber);
        Assert.Equal(7, result.StringTable["line:b"].lineNumber);
        var node = result.Program!.Nodes["B"];
        var lineIndex = node.Instructions.Select((instruction, index) => (instruction, index))
            .Single(value => value.instruction.RunLine is not null).index;
        Assert.Equal(6, result.ProjectDebugInfo!.GetNodeDebugInfo("B")!.GetLineInfo(lineIndex).Range.Start.Line);
    }

    private static YarnSourceSnapshot Snapshot(string source) => new("test", "test.yarnproject", 3, "en",
        new[] { new YarnSourceFileSnapshot("test.yarn", "test.yarn", source, "hash") }, "hash");
}

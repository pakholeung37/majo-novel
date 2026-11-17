using Google.Protobuf;
using Majo.YarnBridge;
using Xunit;
using Yarn;
using Yarn.Compiler;

namespace Majo.Runtime.UnitTests;

public sealed class YarnProgramBridgeTests
{
    [Fact]
    public void AdaptedProgramPreservesBranchesChoicesAndNodeJumpsWithoutChangingTheImport()
    {
        var compiled = Compile("""
            title: Start
            ---
            <<if false>>
            Wrong. #line:wrong
            <<else>>
            Right. #line:right
            <<endif>>
            -> First #line:first
                Wrong choice. #line:wrong-choice
            -> Second #line:second
                Selected. #line:selected
            <<jump Next>>
            ===
            title: Next
            ---
            Done. #line:done
            ===
            """);
        var program = Assert.IsType<global::Yarn.Program>(compiled.Program);
        var original = program.ToByteArray();
        var prepared = YarnProgramBridge.WithNodeEntries(program);
        Assert.Equal(original, program.ToByteArray());
        Assert.Equal(prepared.ToByteArray(), YarnProgramBridge.WithNodeEntries(prepared).ToByteArray());

        var dialogue = new global::Yarn.Dialogue(new MemoryVariableStore());
        var lines = new List<string>();
        var entries = new List<string>();
        dialogue.LineHandler = line => lines.Add(line.ID);
        dialogue.CommandHandler = command => entries.Add(command.Text);
        dialogue.OptionsHandler = options => dialogue.SetSelectedOption(options.Options.Last().ID);
        dialogue.SetProgram(prepared);
        dialogue.SetNode("Start");
        for (var step = 0; step < 30 && (step == 0 || dialogue.IsActive); step++) dialogue.Continue();
        Assert.False(dialogue.IsActive);
        Assert.Equal(new[] { "line:right", "line:selected", "line:done" }, lines);
        Assert.Equal(new[] { YarnProgramBridge.NodeEntryCommand, YarnProgramBridge.NodeEntryCommand }, entries);
    }

    [Fact]
    public void SmartVariableEvaluationRemainsSynchronousAndReceivesNoEntryCommand()
    {
        var compiled = Compile("""
            title: Start
            ---
            <<declare $score = 2>>
            <<declare $double = $score * 2>>
            Score {$double}. #line:smart
            ===
            """);
        var program = Assert.IsType<global::Yarn.Program>(compiled.Program);
        Assert.True(program.Nodes.Count > 1);
        var prepared = YarnProgramBridge.WithNodeEntries(program);
        var dialogue = new global::Yarn.Dialogue(new MemoryVariableStore());
        string[]? substitutions = null;
        var entries = 0;
        dialogue.LineHandler = line => substitutions = line.Substitutions;
        dialogue.OptionsHandler = _ => { };
        dialogue.CommandHandler = _ => entries++;
        dialogue.SetProgram(prepared);
        dialogue.SetNode("Start");
        dialogue.Continue();
        dialogue.Continue();
        Assert.Equal(new[] { "4" }, substitutions);
        Assert.Equal(1, entries);
    }

    [Fact]
    public void CompilerBridgePreservesAuthoredStringAndInstructionCoordinates()
    {
        var compiled = Compile("title: Start\n---\nHello #line:coordinates\n===");
        YarnCompilerSourceMap.AddNodeEntries(compiled);
        Assert.Equal(3, compiled.StringTable!["line:coordinates"].lineNumber);
        var instructions = compiled.Program!.Nodes["Start"].Instructions;
        var index = instructions.Select((instruction, index) => (instruction, index))
            .Single(pair => pair.instruction.RunLine is not null).index;
        Assert.Equal(2, compiled.ProjectDebugInfo!.GetNodeDebugInfo("Start")!.GetLineInfo(index).Range.Start.Line);
    }

    [Theory]
    [InlineData("title: Start\n---\n=> Wrong <<if false>> #line:wrong\n=> Chosen <<if true>> #line:chosen\nDone #line:done\n===", new[] { "line:chosen", "line:done" })]
    [InlineData("title: Start\n---\n<<jump Group>>\n===\ntitle: Group\nwhen: false\n---\nWrong #line:wrong\n===\ntitle: Group\nwhen: always\n---\nChosen #line:chosen\n===", new[] { "line:chosen" })]
    [InlineData("title: Start\n---\n<<detour Sub>>\nReturned #line:returned\n===\ntitle: Sub\n---\nInside #line:inside\n===", new[] { "line:inside", "line:returned" })]
    public void SaliencyAndDetoursKeepTheirOriginalControlFlow(string source, string[] expected)
    {
        var program = Assert.IsType<global::Yarn.Program>(Compile(source).Program);
        var dialogue = new global::Yarn.Dialogue(new MemoryVariableStore())
        {
            ContentSaliencyStrategy = new global::Yarn.Saliency.FirstSaliencyStrategy(),
        };
        var lines = new List<string>();
        dialogue.LineHandler = line => lines.Add(line.ID);
        dialogue.OptionsHandler = _ => { };
        dialogue.CommandHandler = _ => { };
        dialogue.SetProgram(YarnProgramBridge.WithNodeEntries(program));
        dialogue.SetNode("Start");
        for (var step = 0; step < 50 && (step == 0 || dialogue.IsActive); step++) dialogue.Continue();
        Assert.False(dialogue.IsActive);
        Assert.Equal(expected, lines);
    }

    private static CompilationResult Compile(string source)
    {
        var result = Compiler.Compile(CompilationJob.CreateFromString("bridge.yarn", source));
        Assert.False(result.ContainsErrors, string.Join("\n", result.Diagnostics));
        return result;
    }
}

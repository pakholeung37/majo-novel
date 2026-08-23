using System.Diagnostics.CodeAnalysis;
using Majo.YarnBridge;
using Yarn;
using Yarn.Compiler;
using Xunit;
using YarnDialogue = Yarn.Dialogue;

namespace Majo.Runtime.UnitTests;

public sealed class YarnVmStateBridgeTests
{
    [Fact]
    public void FriendAssemblyBridgeReplaysCapturedLineAndContinues()
    {
        var program = CreateProgram("line:first", "line:second");
        var dialogue = CreateDialogue(program, out var deliveredLines);
        var bridge = new YarnVmStateBridge();

        dialogue.Continue();
        Assert.Equal(new[] { "line:first" }, deliveredLines);

        var snapshot = bridge.CaptureAtLine(dialogue, "line:first");
        Assert.Equal("Start", snapshot.NodeName);
        Assert.Equal(1, snapshot.CapturedProgramCounter);
        Assert.Equal("WaitingForContinue", snapshot.ExecutionState);
        Assert.Equal(0, snapshot.OperandStackDepth);
        Assert.Equal(0, snapshot.CallStackDepth);
        Assert.Equal(0, snapshot.PendingOptionCount);

        dialogue.Stop();
        bridge.PrepareReplay(dialogue, program, snapshot);

        dialogue.Continue();
        dialogue.Continue();

        Assert.Equal(
            new[] { "line:first", "line:first", "line:second" },
            deliveredLines);
    }

    [Fact]
    public void RestoreUsesLineIdWhenInstructionIndexChanges()
    {
        var original = CreateProgram("line:target", "line:after");
        var dialogue = CreateDialogue(original, out var deliveredLines);
        var bridge = new YarnVmStateBridge();

        dialogue.Continue();
        var snapshot = bridge.CaptureAtLine(dialogue, "line:target");
        dialogue.Stop();

        var changed = CreateProgram("line:inserted", "line:target", "line:after");
        bridge.PrepareReplay(dialogue, changed, snapshot);
        dialogue.Continue();
        dialogue.Continue();

        Assert.Equal(
            new[] { "line:target", "line:target", "line:after" },
            deliveredLines);
    }

    [Fact]
    public void BridgeReplaysLineFromRealYarnCompilerProgram()
    {
        const string source = """
            title: Start
            ---
            Narrator: First compiled line. #line:bridge-first
            Narrator: Second compiled line. #line:bridge-second
            ===
            """;
        var compilation = Compiler.Compile(
            CompilationJob.CreateFromString("bridge.yarn", source));
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error);
        var program = Assert.IsType<Program>(compilation.Program);
        var dialogue = CreateDialogue(program, out var deliveredLines);
        var bridge = new YarnVmStateBridge();

        dialogue.Continue();
        var snapshot = bridge.CaptureAtLine(dialogue, "line:bridge-first");
        dialogue.Stop();

        bridge.PrepareReplay(dialogue, program, snapshot);
        dialogue.Continue();
        dialogue.Continue();

        Assert.Equal(
            new[]
            {
                "line:bridge-first",
                "line:bridge-first",
                "line:bridge-second",
            },
            deliveredLines);
    }

    private static YarnDialogue CreateDialogue(
        Program program,
        out List<string> deliveredLines)
    {
        var lines = new List<string>();
        var dialogue = new YarnDialogue(new FakeVariableStorage())
        {
            LineHandler = line => lines.Add(line.ID),
            CommandHandler = _ => { },
            OptionsHandler = _ => { },
        };
        dialogue.SetProgram(program);
        dialogue.SetNode("Start");
        deliveredLines = lines;
        return dialogue;
    }

    private static Program CreateProgram(params string[] lineIds)
    {
        var node = new Node { Name = "Start" };
        foreach (var lineId in lineIds)
        {
            node.Instructions.Add(new Instruction
            {
                RunLine = new Instruction.Types.RunLineInstruction
                {
                    LineID = lineId,
                    SubstitutionCount = 0,
                },
            });
        }

        var program = new Program();
        program.Nodes.Add(node.Name, node);
        return program;
    }

    private sealed class FakeVariableStorage : IVariableStorage
    {
        private readonly Dictionary<string, object> values = new();

        public Program? Program { get; set; }

        public ISmartVariableEvaluator? SmartVariableEvaluator { get; set; }

        public void SetValue(string variableName, string stringValue) =>
            values[variableName] = stringValue;

        public void SetValue(string variableName, float floatValue) =>
            values[variableName] = floatValue;

        public void SetValue(string variableName, bool boolValue) =>
            values[variableName] = boolValue;

        public bool TryGetValue<T>(
            string variableName,
            [NotNullWhen(true)] out T? result)
        {
            if (values.TryGetValue(variableName, out var value) && value is T typed)
            {
                result = typed;
                return true;
            }

            result = default;
            return false;
        }

        public VariableKind GetVariableKind(string name) =>
            values.ContainsKey(name) ? VariableKind.Stored : VariableKind.Unknown;

        public void Clear() => values.Clear();
    }
}

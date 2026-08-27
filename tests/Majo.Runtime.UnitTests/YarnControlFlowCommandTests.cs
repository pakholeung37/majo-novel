using System.Diagnostics.CodeAnalysis;
using Yarn;
using Yarn.Compiler;
using Xunit;
using YarnDialogue = Yarn.Dialogue;

namespace Majo.Runtime.UnitTests;

public sealed class YarnControlFlowCommandTests
{
    [Fact]
    public void JumpTransfersToTargetWithoutReturningToCaller()
    {
        const string source = """
            title: Start
            ---
            Before jump. #line:jump-before
            <<jump JumpTarget>>
            This must not run. #line:jump-after
            ===
            title: JumpTarget
            ---
            Jump target. #line:jump-target
            ===
            """;

        var lines = RunToCompletion(source);

        Assert.Equal(["line:jump-before", "line:jump-target"], lines);
    }

    [Fact]
    public void DetourRunsTargetAndReturnsToCaller()
    {
        const string source = """
            title: Start
            ---
            Before detour. #line:detour-before
            <<detour DetourTarget>>
            After detour. #line:detour-after
            ===
            title: DetourTarget
            ---
            Detour target. #line:detour-target
            ===
            """;

        var lines = RunToCompletion(source);

        Assert.Equal(
            ["line:detour-before", "line:detour-target", "line:detour-after"],
            lines);
    }

    private static IReadOnlyList<string> RunToCompletion(string source)
    {
        var compilation = Compiler.Compile(
            CompilationJob.CreateFromString("control-flow.yarn", source));
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error);
        var program = Assert.IsType<Program>(compilation.Program);
        var lines = new List<string>();
        var completed = false;
        var dialogue = new YarnDialogue(new FakeVariableStorage())
        {
            LineHandler = line => lines.Add(line.ID),
            CommandHandler = _ => { },
            OptionsHandler = _ => { },
            DialogueCompleteHandler = () => completed = true,
        };
        dialogue.SetProgram(program);
        dialogue.SetNode("Start");

        for (var step = 0; step < 20 && !completed; step++)
        {
            dialogue.Continue();
        }

        Assert.True(completed, "Dialogue did not complete within 20 VM steps.");
        return lines;
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

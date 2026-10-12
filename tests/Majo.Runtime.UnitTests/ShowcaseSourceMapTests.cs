using Majo.Demos.Recording;
using Majo.Runtime.Dialogue.Yarn;
using Majo.YarnBridge;
using Xunit;
using Yarn;
using Yarn.Compiler;

namespace Majo.Runtime.UnitTests;

public sealed class ShowcaseSourceMapTests
{
    [Fact]
    public void CursorMapsRepeatedCommandsAcrossBranchesAndDetoursToAuthoredLines()
    {
        const string source = """
            title: Start
            ---
            <<declare $take_branch = false>>
            <<if $take_branch>>
                <<marker same>>
            <<else>>
                <<marker same>>
            <<endif>>
            <<detour Sub>>
            <<marker same>>
            End. #line:end
            ===
            title: Sub
            ---
            <<marker same>>
            Inside. #line:inside
            ===
            """;
        var transform = new YarnNodeEntryTransform();
        var library = new Library();
        var compilation = Compiler.Compile(CompilationJob.CreateFromInputs(
            new[] { transform.Transform("recording.yarn", source) }, library, 3));
        Assert.False(compilation.ContainsErrors);
        var map = new ShowcaseSourceMap(new[] { ("recording.yarn", source) }, library, 3, compilation.Program!);
        var dialogue = new global::Yarn.Dialogue(new MemoryVariableStore());
        var seen = new List<(string Kind, string Node, int Line)>();
        void Observe(string kind)
        {
            var (node, instruction) = YarnVmStateBridge.GetExecutionPosition(dialogue);
            var position = map.Resolve(node, instruction);
            Assert.NotNull(position);
            Assert.Equal("recording.yarn", position.File);
            seen.Add((kind, position.Node, position.Line));
        }
        dialogue.CommandHandler = command =>
        {
            if (command.Text != YarnNodeEntryTransform.CommandName) Observe("command");
        };
        dialogue.LineHandler = _ => Observe("dialogue");
        dialogue.OptionsHandler = _ => throw new InvalidOperationException("Unexpected options in source-map fixture.");
        dialogue.SetProgram(compilation.Program!);
        dialogue.SetNode("Start");
        while (dialogue.IsActive || seen.Count == 0) dialogue.Continue();
        Assert.Equal(new[]
        {
            ("command", "Start", 7),
            ("command", "Sub", 15),
            ("dialogue", "Sub", 16),
            ("command", "Start", 10),
            ("dialogue", "Start", 11),
        }, seen);
    }

    [Fact]
    public void DifferentImportedBytecodeIsRejectedInsteadOfShowingIncorrectSource()
    {
        var transform = new YarnNodeEntryTransform();
        var original = Compiler.Compile(CompilationJob.CreateFromInputs(new[]
        {
            transform.Transform("recording.yarn", "title: Start\n---\n<<marker old>>\n===")
        }));
        Assert.Throws<InvalidOperationException>(() => new ShowcaseSourceMap(
            new[] { ("recording.yarn", "title: Start\n---\n<<marker new>>\n===") }, new Library(), 3, original.Program!));
    }

    [Fact]
    public void HighlightLineColorsCommandsParametersSpeakersMarkupAndLineTags()
    {
        var commandLine = "<<camera move view:main preset:leia_close duration:0.6 ease:cubic-out>>";
        var commandColors = ScriptRecordingPanel.HighlightLine(commandLine);
        Assert.Equal(commandLine.Length, commandColors.Length);
        Assert.NotEqual(commandColors[0], commandColors[2]); // '<<' vs 'camera'
        Assert.NotEqual(commandColors[2], commandColors[9]); // 'camera' vs 'move'
        Assert.NotEqual(commandColors[14], commandColors[19]); // 'view' key vs 'main' value
        Assert.NotEqual(commandColors[42], commandColors[51]); // 'duration' key vs '0.6' number

        var dialogueLine = "Leia: 好，[br /]雪莉。 #line:0101Trial00_Leia002";
        var dialogueColors = ScriptRecordingPanel.HighlightLine(dialogueLine);
        Assert.Equal(dialogueLine.Length, dialogueColors.Length);
        var speakerColor = dialogueColors[0];
        var textColor = dialogueColors[dialogueLine.IndexOf('好')];
        var markupColor = dialogueColors[dialogueLine.IndexOf('[')];
        var tagColor = dialogueColors[dialogueLine.IndexOf('#')];
        Assert.NotEqual(speakerColor, textColor);
        Assert.NotEqual(textColor, markupColor);
        Assert.NotEqual(textColor, tagColor);
    }
}

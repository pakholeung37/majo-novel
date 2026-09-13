using Majo.Runtime.Dialogue.Yarn.Preloading;
using Xunit;
using Yarn;
using Yarn.Compiler;

namespace Majo.Runtime.UnitTests;

public sealed class YarnPreloadPlannerTests
{
    [Fact]
    public void BackdropReferencesPreserveAppearanceStageAndRepeatedBaselineWrites()
    {
        var planner = Compile("""
            <<backdrop Court appearance:Night stage:other>>
            <<backdrop Court appearance:+Rain stage:other>>
            <<backdrop Court appearance:Night stage:other>>
            <<backdrop Court appearance:+Snow stage:other>>
            """);
        Assert.Equal(new[]
        {
            new YarnPreloadReference("backdrop", "Court", null, "Night", "other"),
            new YarnPreloadReference("backdrop", "Court", null, "+Rain", "other"),
            new YarnPreloadReference("backdrop", "Court", null, "Night", "other"),
            new YarnPreloadReference("backdrop", "Court", null, "+Snow", "other"),
        }, planner.LookAhead());
        Assert.Empty(YarnPreloadPlanner.Resources(["backdrop", "fx", "shake"]));
    }

    [Fact]
    public void UnvoicedNarrationDoesNotConsumeVoiceWindowOrHideInterveningVisuals()
    {
        var planner = Compile("""
            Narration #line:silent1
            Narration #line:silent2
            <<backdrop Screen>>
            Ema: First #line:voice1
            Narration #line:silent3
            Narration #line:silent4
            Ema: Second #line:voice2
            Ema: Third #line:voice3
            """);
        Assert.Equal(new[]
        {
            new YarnPreloadReference("backdrop", "Screen"),
            new YarnPreloadReference("line", "line:voice1"),
            new YarnPreloadReference("line", "line:voice2"),
        }, planner.LookAhead(2, hasVoice: id => id.StartsWith("line:voice", StringComparison.Ordinal)));
        Assert.Empty(planner.LookAhead(4, maxInstructions: 2, hasVoice: _ => false));
    }

    [Fact]
    public void CompiledCommandsAndLinesAdvanceBoundedWindow()
    {
        var planner = Compile("""
            <<backdrop Room>>
            Ema: First #line:first
            <<character Warden appearance:1>>
            Ema: Second #line:second
            Ema: Third #line:third
            """);
        Assert.Equal(new[]
        {
            new YarnPreloadReference("backdrop", "Room"),
            new YarnPreloadReference("line", "line:first"),
            new YarnPreloadReference("character", "Warden", null, "1"),
        }, planner.LookAhead(1));
        planner.Command(new[] { "backdrop", "Room" });
        Assert.DoesNotContain(planner.LookAhead(), item => item.Id == "Room");
        planner.Line("line:second");
        Assert.Equal(new[] { new YarnPreloadReference("line", "line:third") }, planner.LookAhead());
    }

    [Fact]
    public void BranchIsNotSpeculativelyEvaluatedAndLineResynchronizes()
    {
        var planner = Compile("""
            Ema: First #line:first
            <<if true>>
                <<backdrop Secret>>
                Ema: Inside #line:inside
                <<sfx Bell>>
            <<endif>>
            """);
        planner.Line("line:first");
        Assert.Empty(planner.LookAhead());
        planner.Command(new[] { "backdrop", "Secret" });
        Assert.Empty(planner.LookAhead());
        planner.Line("line:inside");
        Assert.Contains(new YarnPreloadReference("sfx", "Bell"), planner.LookAhead());
    }

    [Fact]
    public void DynamicCommandStopsLookAheadButResolvedArgumentsHaveDependencies()
    {
        var planner = Compile("""
            <<declare $room = "Room">>
            Ema: First #line:first
            <<backdrop {$room}>>
            <<backdrop Later>>
            """);
        planner.Line("line:first");
        Assert.Empty(planner.LookAhead());
        Assert.Equal(new[] { new YarnPreloadReference("backdrop", "Room") },
            YarnPreloadPlanner.Resources(new[] { "backdrop", "Room" }));
    }

    [Fact]
    public void DoesNotScanSecondExplicitBeatOrCrossOptions()
    {
        var planner = Compile("""
            <<beat begin>>
            <<backdrop First>>
            <<beat end>>
            <<beat begin>>
            <<backdrop Second>>
            <<beat end>>
            """);
        Assert.Equal(new[] { new YarnPreloadReference("backdrop", "First") }, planner.LookAhead());
        planner = Compile("""
            Ema: Choose #line:choose
            -> Left #line:left
                <<backdrop Left>>
            -> Right #line:right
                <<backdrop Right>>
            """);
        planner.Line("line:choose");
        Assert.Empty(planner.LookAhead());
    }

    [Fact]
    public void UnknownOrRepeatedLineDoesNotGuessAnInstruction()
    {
        var node = new Node();
        node.Instructions.Add(new Instruction { RunLine = new Instruction.Types.RunLineInstruction { LineID = "repeat" } });
        node.Instructions.Add(new Instruction { RunLine = new Instruction.Types.RunLineInstruction { LineID = "repeat" } });
        var planner = new YarnPreloadPlanner();
        planner.Enter(node);
        planner.Line("repeat");
        Assert.Empty(planner.LookAhead());
        planner.Line("missing");
        Assert.Empty(planner.LookAhead());
    }

    [Fact]
    public void StraightLineCharacterAliasesCarryDefinitionAndStageWithoutInventingAppearance()
    {
        var planner = Compile("""
            <<character ema_left asset:Ema stage:main>>
            <<character ema_left appearance:+Angry stage:main>>
            """);

        Assert.Equal(new[]
        {
            new YarnPreloadReference("character", "ema_left", "Ema", null, "main"),
            new YarnPreloadReference("character", "ema_left", "Ema", "+Angry", "main"),
        }, planner.LookAhead());
    }

    [Fact]
    public void LookAheadDoesNotInventDefinitionAfterCreationHasAdvancedPastCursor()
    {
        var planner = Compile("""
            <<character ema_left asset:Ema stage:main>>
            <<character ema_left appearance:Fearful1 stage:main>>
            """);
        planner.Command(["character", "ema_left", "asset:Ema", "stage:main"]);
        var next = Assert.Single(planner.LookAhead());
        Assert.Equal("ema_left", next.Id);
        Assert.Null(next.Asset);
        Assert.Equal("Fearful1", next.Appearance);
    }

    [Fact]
    public void DefinitionInferenceDoesNotCrossStages()
    {
        var planner = Compile("""
            <<character ema_left asset:Ema stage:first>>
            <<character ema_left appearance:Fearful1 stage:second>>
            """);
        var references = planner.LookAhead();
        Assert.Equal("Ema", references[0].Asset);
        Assert.Null(references[1].Asset);
    }

    private static YarnPreloadPlanner Compile(string body)
    {
        var result = Compiler.Compile(CompilationJob.CreateFromString("test.yarn", $"title: Start\n---\n{body}\n===\n"));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error);
        var planner = new YarnPreloadPlanner();
        planner.Enter(Assert.IsType<Yarn.Program>(result.Program).Nodes["Start"]);
        return planner;
    }
}

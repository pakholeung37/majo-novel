using Majo.Runtime.Dialogue.Yarn.Preloading;
using Xunit;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.UnitTests.Testing;
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
        Assert.Empty(new YarnPreloadPlanner(TestPreloadCatalog.Create()).GetResources(["backdrop", "fx", "shake"]));
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
            new YarnPreloadReference("backdrop", "Screen", Stage: "main"),
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
            new YarnPreloadReference("backdrop", "Room", Stage: "main"),
            new YarnPreloadReference("line", "line:first"),
            new YarnPreloadReference("character", "Warden", null, "1", "main"),
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
        Assert.Equal(new[] { new YarnPreloadReference("backdrop", "Room", Stage: "main") },
            new YarnPreloadPlanner(TestPreloadCatalog.Create()).GetResources(new[] { "backdrop", "Room" }));
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
        Assert.Equal(new[] { new YarnPreloadReference("backdrop", "First", Stage: "main") }, planner.LookAhead());
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
        var planner = new YarnPreloadPlanner(TestPreloadCatalog.Create());
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

    [Fact]
    public void NamedFirstArgumentsAndCasingHaveTheSameMeaningAsFormalBinding()
    {
        var catalog = TestPreloadCatalog.Create();
        string[] tokens = ["CHARACTER", "APPEARANCE:Fearful1", "hero", "ASSET:Ema", "STAGE:other"];
        var command = Assert.IsType<BoundCommand<CharacterCommand.Arguments>>(catalog.Resolve(tokens, out var input));
        var arguments = command.Bind(input!);
        var reference = Assert.Single(new YarnPreloadPlanner(catalog).GetResources(tokens));

        Assert.Equal(arguments.Actor, reference.Id);
        Assert.Equal(arguments.Asset.Value, reference.Asset);
        Assert.Equal(arguments.Appearance.Value, reference.Appearance);
        Assert.Equal(arguments.Stage.Value, reference.Stage);
        Assert.Equal("main", Assert.Single(new YarnPreloadPlanner(catalog).GetResources(["character", "hero"])).Stage);
    }

    [Theory]
    [InlineData("character", "MOVE")]
    [InlineData("character", "HIDE")]
    [InlineData("character", "FX")]
    [InlineData("object", "MOVE")]
    [InlineData("object", "HIDE")]
    [InlineData("object", "FX")]
    [InlineData("backdrop", "FX")]
    public void NonResourceSubcommandsNeverBecomeResourceIds(string root, string child)
    {
        var catalog = TestPreloadCatalog.Create();
        string[] tokens = [root, child, "hero"];
        Assert.Equal($"{root} {child.ToLowerInvariant()}", catalog.Resolve(tokens, out _)!.Name);
        Assert.Empty(new YarnPreloadPlanner(catalog).GetResources(tokens));
    }

    [Theory]
    [InlineData("bgm")]
    [InlineData("sfx")]
    [InlineData("voice")]
    public void AudioResourcesUseTheBoundPrimaryRegardlessOfNamedArgumentOrder(string kind)
    {
        var planner = new YarnPreloadPlanner(TestPreloadCatalog.Create());
        Assert.Equal(new YarnPreloadReference(kind, "Soft Bell"),
            Assert.Single(planner.GetResources([kind, "volume:0.5", "Soft Bell"])));
    }

    [Fact]
    public void QuotedAudioIdSurvivesCompiledLookAheadAndNamedFirstBinding()
    {
        var planner = Compile("""<<sfx volume:0.5 "Soft Bell">>""");
        Assert.Equal([new YarnPreloadReference("sfx", "Soft Bell")], planner.LookAhead());
    }

    [Fact]
    public void InvalidBindingOrBrokenProviderDoesNotDiscardLaterStaticReferences()
    {
        var broken = new PluginResourceCommand { FailDescription = true };
        var planner = Compile("""
            <<character hero scale:invalid>>
            <<character hero asset:Ema ASSET:Other>>
            <<plugin show 42>>
            <<backdrop Room>>
            """, TestPreloadCatalog.Create(broken));
        Assert.Equal([new YarnPreloadReference("backdrop", "Room", Stage: "main")], planner.LookAhead());
        Assert.Equal(0, broken.Executions);
    }

    [Fact]
    public async Task CustomCommandDescriptionsUseTypedValuesAndContractDefaultsWithoutExecuting()
    {
        var plugin = new PluginResourceCommand();
        var catalog = TestPreloadCatalog.Create(plugin);
        var planner = Compile("<<plugin show 42>>", catalog);
        Assert.Equal([new YarnPreloadReference("sfx", "clip-42-calm")], planner.LookAhead());
        Assert.Equal(0, plugin.Executions);

        var command = catalog.Resolve(["PLUGIN", "SHOW", "STYLE:angry", "43"], out var input)!;
        Assert.Equal([new CommandResourceReference("sfx", "clip-43-angry")], command.DescribeResources(input!));
        Assert.Equal(0, plugin.Executions);
        await command.ExecuteAsync(input!, new CommandExecutionContext("execution"));
        Assert.Equal(1, plugin.Executions);
    }

    [Fact]
    public void ObjectAliasesCarryDefinitionAndStageJustLikeCharacters()
    {
        var planner = Compile("""
            <<object evidence asset:knife stage:other>>
            <<object appearance:blue evidence stage:other>>
            """);
        Assert.Equal(new[]
        {
            new YarnPreloadReference("object", "evidence", "knife", Stage: "other"),
            new YarnPreloadReference("object", "evidence", "knife", "blue", "other"),
        }, planner.LookAhead());
    }

    [Fact]
    public void CanonicalCommandPathAdvancesCursorWithoutIgnoringResourceIdCase()
    {
        var planner = Compile("""
            <<character hero asset:Ema>>
            <<character move hero at:left>>
            <<backdrop Room>>
            """);
        planner.Command(["CHARACTER", "hero", "asset:Ema"]);
        planner.Command(["character", "MOVE", "hero", "at:left"]);
        Assert.Equal([new YarnPreloadReference("backdrop", "Room", Stage: "main")], planner.LookAhead());
        planner = Compile("<<character hero asset:Ema>>");
        planner.Command(["character", "Hero", "asset:Ema"]);
        Assert.Empty(planner.LookAhead());
    }

    [Fact]
    public void UppercaseBeatBoundariesStillLimitLookAhead()
    {
        var planner = Compile("""
            <<BEAT BEGIN>>
            <<backdrop First>>
            <<BEAT END>>
            <<BEAT BEGIN>>
            <<backdrop Second>>
            """);
        Assert.Equal([new YarnPreloadReference("backdrop", "First", Stage: "main")], planner.LookAhead());
    }

    private sealed class PluginResourceCommand : RuntimeCommand<PluginResourceCommand.Arguments>,
        ICommandResourceProvider<PluginResourceCommand.Arguments>
    {
        public sealed record Arguments(int Number, CommandValue<string> Style);
        private static readonly CommandDefinition<Arguments> Schema = CommandDefinition<Arguments>.Create("plugin show")
            .Primary<int>("number", memberName: "Number")
            .Optional<string>("style", memberName: "Style", defaultText: "calm")
            .Build(context =>
            {
                var number = context.GetMemberSlot("Number");
                var style = context.GetMemberSlot("Style");
                return reader => new Arguments(reader.ReadRequired<int>(number), reader.ReadOptional<string>(style));
            });
        public override CommandDefinition<Arguments> Definition => Schema;
        public int Executions { get; private set; }
        public bool FailDescription { get; init; }
        public IReadOnlyList<CommandResourceReference> DescribeResources(Arguments arguments) => FailDescription
            ? throw new InvalidOperationException("provider failed")
            : [new("sfx", $"clip-{arguments.Number}-{arguments.Style.Value}")];
        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            Executions++;
            return ValueTask.CompletedTask;
        }
    }

    private static YarnPreloadPlanner Compile(string body, RuntimeCommandCatalog? catalog = null)
    {
        var result = Compiler.Compile(CompilationJob.CreateFromString("test.yarn", $"title: Start\n---\n{body}\n===\n"));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error);
        var planner = new YarnPreloadPlanner(catalog ?? TestPreloadCatalog.Create());
        planner.Enter(Assert.IsType<Yarn.Program>(result.Program).Nodes["Start"]);
        return planner;
    }
}

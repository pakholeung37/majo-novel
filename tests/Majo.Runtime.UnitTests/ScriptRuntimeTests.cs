using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.History;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ScriptRuntimeTests
{
    [Fact]
    public async Task RestoreCheckpoint_ValidatesBeforeStoppingOrPreparingRestore()
    {
        var order = new List<string>();
        var dialogue = new FakeDialoguePlayer(order);
        var checkpointBackend = new FakeCheckpointBackend(order)
        {
            ValidationFailure = new ScriptCheckpointException("invalid checkpoint"),
        };
        var playback = new ScriptPlayback(
            dialogue,
            new FakeDialoguePresentation(),
            checkpointBackend);
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            "fingerprint",
            new ScriptPlaybackSpot(new ScriptId("project", "Start"), "line:1"));

        await Assert.ThrowsAsync<ScriptCheckpointException>(() =>
            playback.RestoreCheckpointAsync(checkpoint));

        Assert.Equal(["validate"], order);
    }

    [Fact]
    public void ScriptIdentityAndCheckpointNormalizeStableValues()
    {
        var id = new ScriptId(" chapter ", " Start ");
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            " fingerprint ",
            new ScriptPlaybackSpot(id, " line:intro "),
            ["Maya", "7", "True"]);

        Assert.Equal("chapter/Start", id.ToString());
        Assert.Equal("fingerprint", checkpoint.ProgramFingerprint);
        Assert.Equal("line:intro", checkpoint.Spot.LineId);
        Assert.Equal(["Maya", "7", "True"], checkpoint.ResolvedSubstitutions);
    }

    [Fact]
    public void ScriptDescriptorRejectsDuplicateStableLineIds()
    {
        var exception = Assert.Throws<ScriptCheckpointException>(() =>
            new ScriptDescriptor(
                new ScriptId("chapter", "Start"),
                "fingerprint",
                ["line:duplicate", "line:duplicate"]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptDescriptorCarriesLineSubstitutionShape()
    {
        var descriptor = new ScriptDescriptor(
            new ScriptId("chapter", "Start"),
            "fingerprint",
            ["line:intro"],
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["line:intro"] = 2,
            });

        Assert.Equal(2, descriptor.LineSubstitutionCounts["line:intro"]);
    }


    [Fact]
    public async Task ScriptPlayback_PresentationEventsDoNotOverrideStoppingOrIdlePhases()
    {
        var dialogue = new FakeDialoguePlayer([]);
        var presentation = new FakeDialoguePresentation();
        var checkpointBackend = new FakeCheckpointBackend([]);
        var playback = new ScriptPlayback(
            dialogue,
            presentation,
            checkpointBackend);
        await playback.InitializeAsync(CancellationToken.None);

        Assert.Equal(ScriptPlaybackPhase.Idle, playback.Phase);
        presentation.FireDisplayCompleted();
        Assert.Equal(ScriptPlaybackPhase.Idle, playback.Phase);
        presentation.FireLineDismissed();
        Assert.Equal(ScriptPlaybackPhase.Idle, playback.Phase);

        dialogue.SetState(DialogueSessionState.Stopping);
        Assert.Equal(ScriptPlaybackPhase.Stopping, playback.Phase);
        presentation.FireDisplayCompleted();
        Assert.Equal(ScriptPlaybackPhase.Stopping, playback.Phase);
        presentation.FireLineDismissed();
        Assert.Equal(ScriptPlaybackPhase.Stopping, playback.Phase);

        dialogue.SetState(DialogueSessionState.Running);
        presentation.IsLineDisplayComplete = true;
        presentation.FireDisplayCompleted();
        Assert.Equal(ScriptPlaybackPhase.AwaitingAdvance, playback.Phase);
    }

    private sealed class FakeCheckpointBackend : IScriptCheckpointBackend
    {
        private readonly List<string> order;

        public FakeCheckpointBackend(List<string> order)
        {
            this.order = order;
        }

        public Exception? ValidationFailure { get; init; }

        public ScriptCheckpoint CaptureCheckpoint() => throw new NotSupportedException();

        public Task ValidateCheckpointAsync(
            ScriptCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            order.Add("validate");
            return ValidationFailure is null
                ? Task.CompletedTask
                : Task.FromException(ValidationFailure);
        }

        public void PrepareRestore(ScriptCheckpoint checkpoint) => order.Add("prepare");

        public void CancelPreparedRestore(ScriptCheckpoint checkpoint) => order.Add("cancel");
    }

}

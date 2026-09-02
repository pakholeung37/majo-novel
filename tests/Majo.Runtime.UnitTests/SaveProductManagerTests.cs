using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SaveProductManagerTests
{
    [Fact]
    public async Task ConfiguredCandidateCommitsOnlyAtAwaitingAdvanceAndOnlyOnce()
    {
        var slots = new FakeSlots();
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var script = new FakeScriptPlayback();
        var backend = new FakeInputBackend();
        var input = new InputManager(backend);
        await input.InitializeAsync(CancellationToken.None);
        var product = new SaveProductManager(
            slots,
            dialogue,
            presentation,
            script,
            input,
            autoSaveMetadata: ["autosave"]);
        await product.InitializeAsync(CancellationToken.None);
        var line = new DialogueLine(
            "line-1",
            "Alice",
            new DialogueText("Text", []),
            ["autosave"]);

        script.SetLine("project", "node", line.Id, ScriptPlaybackPhase.PresentingLine);
        dialogue.Present("project", "node", line);
        Assert.NotNull(product.PendingAutoSave);
        Assert.Equal(0, slots.AutoSaveCount);

        script.SetLine("project", "node", line.Id, ScriptPlaybackPhase.AwaitingAdvance);
        presentation.CompleteLine();
        presentation.CompleteLine();

        Assert.Equal(1, slots.AutoSaveCount);
        Assert.Equal(1, product.LastCommittedAutoSaveBoundary);
        Assert.Null(product.PendingAutoSave);
    }

    [Fact]
    public async Task ProjectCandidateTargetsAssociatedLineAndQuickSaveUsesIndependentChain()
    {
        var slots = new FakeSlots();
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var script = new FakeScriptPlayback();
        var backend = new FakeInputBackend();
        var input = new InputManager(backend);
        await input.InitializeAsync(CancellationToken.None);
        var product = new SaveProductManager(slots, dialogue, presentation, script, input);
        await product.InitializeAsync(CancellationToken.None);

        product.RequestAutoSaveCandidate("boss-defeated");
        var line = new DialogueLine(
            "line-2",
            null,
            new DialogueText("After boss", []),
            []);
        script.SetLine("project", "ending", line.Id, ScriptPlaybackPhase.PresentingLine);
        dialogue.Present("project", "ending", line);
        script.SetLine("project", "ending", line.Id, ScriptPlaybackPhase.AwaitingAdvance);
        presentation.CompleteLine();
        await product.QuickSaveAsync();

        Assert.Equal(1, slots.AutoSaveCount);
        Assert.Equal(1, slots.QuickSaveCount);
        Assert.Equal("boss-defeated", slots.LastAutoRequest?.DeveloperMetadata?["autoSaveSource"]);
    }

    [Fact]
    public async Task ScriptSaveAndAutoSaveCandidatesUseIndependentSlotKinds()
    {
        var slots = new FakeSlots();
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var script = new FakeScriptPlayback();
        var input = new InputManager(new FakeInputBackend());
        await input.InitializeAsync(CancellationToken.None);
        var product = new SaveProductManager(slots, dialogue, presentation, script, input);
        await product.InitializeAsync(CancellationToken.None);

        product.RequestQuickSaveCandidate("command:save");
        product.RequestAutoSaveCandidate("command:autoSave");
        var line = new DialogueLine("line-3", null, new DialogueText("Checkpoint", []), []);
        script.SetLine("project", "node", line.Id, ScriptPlaybackPhase.PresentingLine);
        dialogue.Present("project", "node", line);
        script.SetLine("project", "node", line.Id, ScriptPlaybackPhase.AwaitingAdvance);
        presentation.CompleteLine();

        Assert.Equal(1, slots.QuickSaveCount);
        Assert.Equal(1, slots.AutoSaveCount);
        Assert.Equal("command:save", slots.LastQuickRequest?.DeveloperMetadata?["saveSource"]);
        Assert.Equal("command:autoSave", slots.LastAutoRequest?.DeveloperMetadata?["autoSaveSource"]);
    }

    [Fact]
    public async Task CandidateRequestedBetweenLinesTargetsTheFollowingBoundary()
    {
        var slots = new FakeSlots();
        var dialogue = new FakeDialoguePlayer();
        var presentation = new FakeDialoguePresentation();
        var script = new FakeScriptPlayback();
        var input = new InputManager(new FakeInputBackend());
        await input.InitializeAsync(CancellationToken.None);
        var product = new SaveProductManager(slots, dialogue, presentation, script, input);
        await product.InitializeAsync(CancellationToken.None);

        var previous = new DialogueLine("line-previous", null, new DialogueText("Before", []), []);
        dialogue.Present("project", "node", previous);
        presentation.DismissLine();
        product.RequestQuickSaveCandidate("command:save");

        var next = new DialogueLine("line-next", null, new DialogueText("After", []), []);
        script.SetLine("project", "node", next.Id, ScriptPlaybackPhase.PresentingLine);
        dialogue.Present("project", "node", next);
        script.SetLine("project", "node", next.Id, ScriptPlaybackPhase.AwaitingAdvance);
        presentation.CompleteLine();

        Assert.Equal(1, slots.QuickSaveCount);
        Assert.Equal("command:save", slots.LastQuickRequest?.DeveloperMetadata?["saveSource"]);
    }

    private sealed class FakeSlots : ISaveSlotManager
    {
        public event Action<SaveSlotOperationInfo>? OperationStarted { add { } remove { } }
        public event Action<SaveSlotOperationInfo>? OperationProgressed { add { } remove { } }
        public event Action<SaveSlotOperationInfo>? OperationCompleted { add { } remove { } }
        public event Action<SaveSlotOperationInfo>? OperationFailed { add { } remove { } }
        public event Action? SlotsChanged { add { } remove { } }
        public SaveSlotManagerStatus Status => SaveSlotManagerStatus.Idle;
        public SaveSlotOperationInfo? CurrentOperation => null;
        public SaveSlotId? CurrentSlot => null;
        public IReadOnlyDictionary<SaveSlotKind, int> Capacities { get; } =
            new Dictionary<SaveSlotKind, int>
            {
                [SaveSlotKind.Manual] = 1,
                [SaveSlotKind.Quick] = 2,
                [SaveSlotKind.Auto] = 2,
            };
        public int AutoSaveCount { get; private set; }
        public int QuickSaveCount { get; private set; }
        public SaveSlotSaveRequest? LastRequest { get; private set; }
        public SaveSlotSaveRequest? LastQuickRequest { get; private set; }
        public SaveSlotSaveRequest? LastAutoRequest { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public SaveSlotAvailability CanSave(SaveSlotId slot) => new([]);
        public Task<SaveSlotAvailability> CanLoadAsync(
            SaveSlotId slot,
            CancellationToken cancellationToken = default) => Task.FromResult(new SaveSlotAvailability([]));
        public Task<IReadOnlyList<SaveSlotMetadata>> ListAsync(
            SaveSlotKind? kind = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SaveSlotMetadata>>([]);
        public Task<SaveSlotMetadata> ReadMetadataAsync(
            SaveSlotId slot,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SaveSlotMetadata> SaveAsync(
            SaveSlotId slot,
            SaveSlotSaveRequest? request = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SaveSlotMetadata> SaveRotatingAsync(
            SaveSlotKind kind,
            SaveSlotSaveRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (kind == SaveSlotKind.Auto)
            {
                AutoSaveCount++;
                LastAutoRequest = request;
            }
            else if (kind == SaveSlotKind.Quick)
            {
                QuickSaveCount++;
                LastQuickRequest = request;
            }
            return Task.FromResult(CreateMetadata(new SaveSlotId(kind, 1)));
        }
        public Task LoadAsync(
            SaveSlotId slot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(
            SaveSlotId slot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SaveSlotMetadata> RenameAsync(
            SaveSlotId slot,
            string? playerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateMetadata(slot));
        private static SaveSlotMetadata CreateMetadata(SaveSlotId id) => new(
            id,
            SaveSlotCompatibility.Compatible,
            DateTimeOffset.UtcNow,
            "game",
            "runtime",
            "project",
            "node",
            "line",
            null,
            "text",
            null,
            null,
            null,
            new Dictionary<string, string>());
    }

}

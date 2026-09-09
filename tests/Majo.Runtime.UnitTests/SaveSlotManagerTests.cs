using System.Text.Json;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SaveSlotManagerTests
{
    [Fact]
    public async Task SaveMetadataUsesCapturedEntryEvenIfPlaybackAdvancesBeforeCaptureReturns()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.State.Value = 7;
        fixture.State.AdvanceAfterCapture = true;
        var slot = new SaveSlotId(SaveSlotKind.Manual, 1);
        await fixture.Manager.SaveAsync(slot);
        var envelope = fixture.Serializer.Deserialize(await fixture.Storage.ReadAsync("slots/manual/0001"));
        Assert.Equal(7, envelope.Snapshot.Records["game.fake"].Payload.GetProperty("value").GetInt32());
        Assert.Equal("Line 7", envelope.DisplayMetadata[SaveSlotMetadataKeys.TextSummary]);
        Assert.Equal("Line 8", fixture.State.CurrentBoundary!.Context.Line.Text.Text);
    }

    [Fact]
    public async Task SaveOverwriteFailurePreservesOldSlotAndCleansNewThumbnail()
    {
        var fixture = await Fixture.CreateAsync();
        var slot = new SaveSlotId(SaveSlotKind.Manual, 1);
        fixture.State.Value = 7;
        var original = await fixture.Manager.SaveAsync(slot);
        var originalBytes = await fixture.Storage.ReadAsync("slots/manual/0001");

        fixture.State.Value = 99;
        fixture.Storage.FailNextWrite = true;
        await Assert.ThrowsAsync<StateStorageException>(() => fixture.Manager.SaveAsync(slot));

        Assert.Equal(originalBytes, await fixture.Storage.ReadAsync("slots/manual/0001"));
        var after = await fixture.Manager.ReadMetadataAsync(slot);
        Assert.Equal(original.SavedAt, after.SavedAt);
        Assert.Contains("thumbnail-2.png", fixture.Thumbnails.Deleted);
        Assert.Equal(SaveSlotOperationPhase.Commit, fixture.Manager.LastFailurePhase);
    }

    [Fact]
    public async Task RotatingChainsUseFreeSlotsThenReplaceOldestDeterministically()
    {
        var fixture = await Fixture.CreateAsync(quickCapacity: 2);
        fixture.State.Value = 1;
        var first = await fixture.Manager.SaveRotatingAsync(SaveSlotKind.Quick);
        fixture.State.Value = 2;
        var second = await fixture.Manager.SaveRotatingAsync(SaveSlotKind.Quick);
        fixture.State.Value = 3;
        var third = await fixture.Manager.SaveRotatingAsync(SaveSlotKind.Quick);

        Assert.Equal(1, first.Id.Index);
        Assert.Equal(2, second.Id.Index);
        Assert.Equal(1, third.Id.Index);
        var listed = await fixture.Manager.ListAsync(SaveSlotKind.Quick);
        Assert.Equal(2, listed.Count);
        Assert.Equal([1, 2], listed.Select(value => value.Id.Index).Order());
        Assert.True(listed.Single(value => value.Id.Index == 1).SavedAt >= second.SavedAt);
    }

    [Fact]
    public async Task RenameChangesOnlyPlayerFacingNameAndDeleteIsPreciselyScoped()
    {
        var fixture = await Fixture.CreateAsync();
        var first = new SaveSlotId(SaveSlotKind.Manual, 1);
        var second = new SaveSlotId(SaveSlotKind.Manual, 2);
        await fixture.Manager.SaveAsync(first, new SaveSlotSaveRequest("Before"));
        await fixture.Manager.SaveAsync(second);
        var firstEnvelope = fixture.Serializer.Deserialize(
            await fixture.Storage.ReadAsync("slots/manual/0001"));

        var renamed = await fixture.Manager.RenameAsync(first, "Chapter One");
        var renamedEnvelope = fixture.Serializer.Deserialize(
            await fixture.Storage.ReadAsync("slots/manual/0001"));

        Assert.Equal("Chapter One", renamed.PlayerName);
        Assert.Equal(firstEnvelope.CreatedAt, renamedEnvelope.CreatedAt);
        Assert.Equal(
            firstEnvelope.Snapshot.Records.Values.Single().Payload.GetRawText(),
            renamedEnvelope.Snapshot.Records.Values.Single().Payload.GetRawText());

        await fixture.Manager.DeleteAsync(first);
        Assert.Single(await fixture.Manager.ListAsync());
        Assert.Equal(second, (await fixture.Manager.ListAsync()).Single().Id);
        Assert.Contains("thumbnail-1.png", fixture.Thumbnails.Deleted);
    }

    [Fact]
    public async Task CorruptOrIncompatibleLoadDoesNotReachPreflightOrSessionMutation()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.Storage.WriteAsync("slots/manual/0001", "not-json"u8.ToArray());

        var corrupt = await fixture.Manager.ReadMetadataAsync(
            new SaveSlotId(SaveSlotKind.Manual, 1));
        Assert.Equal(SaveSlotCompatibility.Corrupt, corrupt.Compatibility);
        await Assert.ThrowsAnyAsync<StateRuntimeException>(
            () => fixture.Manager.LoadAsync(corrupt.Id));
        Assert.Equal(0, fixture.State.Transaction.PreflightCount);
        Assert.Equal(0, fixture.Sessions.LoadCount);
        Assert.Null(fixture.Manager.CurrentSlot);

        var foreignSlot = new SaveSlotId(SaveSlotKind.Manual, 2);
        var foreignSnapshot = fixture.State.CreateSnapshot();
        var foreign = fixture.State.CreateEnvelope(
            foreignSnapshot,
            StateScope.Game,
            "0.1.0",
            "game",
            "foreign/namespace",
            new Dictionary<string, string>
            {
                [SaveSlotMetadataKeys.SlotKind] = SaveSlotKind.Manual.ToString(),
                [SaveSlotMetadataKeys.SlotIndex] = "2",
            });
        await fixture.Storage.WriteAsync(
            "slots/manual/0002",
            fixture.Serializer.Serialize(foreign));
        var incompatible = await fixture.Manager.ReadMetadataAsync(foreignSlot);
        Assert.Equal(SaveSlotCompatibility.Incompatible, incompatible.Compatibility);
        await Assert.ThrowsAsync<StateCompatibilityException>(
            () => fixture.Manager.LoadAsync(foreignSlot));
        Assert.Equal(0, fixture.Sessions.LoadCount);
    }

    [Fact]
    public async Task ValidLoadPreflightsBeforeBeginningProductLoadAndTracksCurrentSlot()
    {
        var fixture = await Fixture.CreateAsync();
        var slot = new SaveSlotId(SaveSlotKind.Manual, 1);
        await fixture.Manager.SaveAsync(slot);
        fixture.State.Transaction.OnPreflight = () =>
            Assert.Equal(0, fixture.Sessions.LoadCount);

        await fixture.Manager.LoadAsync(slot);

        Assert.Equal(1, fixture.State.Transaction.PreflightCount);
        Assert.Equal(1, fixture.Sessions.LoadCount);
        Assert.Equal(slot, fixture.Manager.CurrentSlot);
        Assert.Equal(SaveSlotManagerStatus.Idle, fixture.Manager.Status);
    }

    [Fact]
    public async Task CapacityAndAvailabilityAreTyped()
    {
        var fixture = await Fixture.CreateAsync();
        var invalid = fixture.Manager.CanSave(new SaveSlotId(SaveSlotKind.Manual, 4));
        Assert.False(invalid.IsAllowed);
        Assert.Contains(invalid.Reasons,
            reason => reason.Code == SaveSlotUnavailableReasonCode.InvalidSlot);
        var missing = await fixture.Manager.CanLoadAsync(
            new SaveSlotId(SaveSlotKind.Manual, 1));
        Assert.Contains(missing.Reasons,
            reason => reason.Code == SaveSlotUnavailableReasonCode.NotFound);
    }

    [Fact]
    public async Task ObserverFailureCannotTurnCommittedSaveIntoFailure()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Manager.OperationStarted += _ => throw new InvalidOperationException("started");
        fixture.Manager.OperationProgressed += _ => throw new InvalidOperationException("progress");
        fixture.Manager.OperationCompleted += _ => throw new InvalidOperationException("completed");
        fixture.Manager.SlotsChanged += () => throw new InvalidOperationException("changed");
        var slot = new SaveSlotId(SaveSlotKind.Manual, 1);

        var saved = await fixture.Manager.SaveAsync(slot);

        Assert.Equal(slot, saved.Id);
        Assert.Equal(SaveSlotManagerStatus.Idle, fixture.Manager.Status);
        Assert.Equal(SaveSlotCompatibility.Compatible,
            (await fixture.Manager.ReadMetadataAsync(slot)).Compatibility);
    }

    private sealed class Fixture
    {
        private Fixture(int quickCapacity)
        {
            State = new FakeStateManager();
            Storage = new MemoryStorage();
            Serializer = new CanonicalJsonStateSerializer();
            Eligibility = new FakeEligibility();
            Sessions = new FakeSessions();
            Thumbnails = new FakeThumbnails();
            Manager = new SaveSlotManager(
                State,
                Serializer,
                Storage,
                Eligibility,
                Sessions,
                new FakeHistory(),
                new Dictionary<SaveSlotKind, int>
                {
                    [SaveSlotKind.Manual] = 3,
                    [SaveSlotKind.Quick] = quickCapacity,
                    [SaveSlotKind.Auto] = 2,
                },
                "0.1.0",
                "game",
                "test/state",
                Thumbnails);
        }

        public FakeStateManager State { get; }
        public MemoryStorage Storage { get; }
        public CanonicalJsonStateSerializer Serializer { get; }
        public FakeEligibility Eligibility { get; }
        public FakeSessions Sessions { get; }
        public FakeThumbnails Thumbnails { get; }
        public SaveSlotManager Manager { get; }

        public static async Task<Fixture> CreateAsync(int quickCapacity = 2)
        {
            var fixture = new Fixture(quickCapacity);
            await fixture.Manager.InitializeAsync(CancellationToken.None);
            return fixture;
        }
    }

    private sealed class FakeStateManager : IStateManager, IStateBoundarySource
    {
        public event Action<CommittedLineBoundary>? BoundaryCommitted { add { } remove { } }
        public CommittedLineBoundary? CurrentBoundary { get; private set; }
        public bool AdvanceAfterCapture { get; set; }
        public Task<CommittedLineBoundary> CaptureSaveBoundaryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = MakeBoundary();
            CurrentBoundary = captured;
            if (AdvanceAfterCapture)
            {
                Value++;
                CurrentBoundary = MakeBoundary();
            }
            return Task.FromResult(captured);
        }
        private CommittedLineBoundary MakeBoundary() => new(
            new LineBoundaryContext(1, Value,
                new ScriptCheckpoint(ScriptCheckpoint.CurrentFormatVersion, "fingerprint",
                    new ScriptPlaybackSpot(new ScriptId("project", "node"), "line")),
                new DialogueLine("line", "Narrator", new DialogueText($"Line {Value}", []), [])),
            CreateSnapshot());
        public event Action<StateStatusChanged>? StatusChanged { add { } remove { } }
        public event Action<StateSnapshot>? Captured { add { } remove { } }
        public event Action<StateSnapshot>? Restored { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public StateManagerStatus Status => StateManagerStatus.Idle;
        public StateSnapshot? CurrentSnapshot { get; private set; }
        public FakeRestoreTransaction Transaction { get; } = new();
        public IStateRestoreTransaction RestoreTransaction => Transaction;
        public int Value { get; set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StateSnapshot> CaptureAsync(
            StateScope scope,
            CancellationToken cancellationToken = default)
        {
            CurrentSnapshot = CreateSnapshot();
            return Task.FromResult(CurrentSnapshot);
        }
        public Task<StateSnapshot> CaptureGameAsync(
            CancellationToken cancellationToken = default) =>
            CaptureAsync(StateScope.Game, cancellationToken);
        public StateSnapshot CreateSnapshot()
        {
            var record = new StateRecord(
                "game.fake",
                StateScope.Game,
                1,
                JsonSerializer.SerializeToElement(new { value = Value }));
            return new StateSnapshot(1, DateTimeOffset.UtcNow, [record]);
        }
        public StateEnvelope CreateEnvelope(
            StateSnapshot snapshot,
            StateScope scope,
            string runtimeVersion,
            string gameBuildId,
            string storageNamespace,
            IReadOnlyDictionary<string, string>? displayMetadata = null) => new(
                1,
                runtimeVersion,
                gameBuildId,
                storageNamespace,
                DateTimeOffset.UtcNow,
                scope,
                snapshot,
                snapshot.Records.Values.Select(record => new StateEnvelopeRecordMetadata(
                    record.Key,
                    record.SchemaVersion,
                    StateContributorCapability.DurableRollback)),
                new StateScriptSummary("project", "node", "line", "fingerprint"),
                displayMetadata);
        public Task ResetScopeAsync(
            StateScope scope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreGameAsync(
            StateSnapshot snapshot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRestoreTransaction : IStateRestoreTransaction
    {
        public int PreflightCount { get; private set; }
        public Action? OnPreflight { get; set; }
        public Task<StateRestorePlan> PreflightAsync(
            StateSnapshot snapshot,
            StateScope scope,
            StateOperationKind operation = StateOperationKind.Restore,
            CancellationToken cancellationToken = default)
        {
            PreflightCount++;
            OnPreflight?.Invoke();
            return Task.FromResult(new StateRestorePlan(
                snapshot,
                scope,
                operation,
                snapshot.Records));
        }
        public Task ExecuteAsync(
            StateRestorePlan plan,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryStorage : IStateStorage
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public bool FailNextWrite { get; set; }
        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(key, out var value)
                ? value.ToArray()
                : throw new StateStorageNotFoundException(key));
        public Task WriteAsync(
            string key,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new StateStorageException("Simulated write failure.");
            }
            values[key] = content.ToArray();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(values.Keys.Order().ToArray());
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!values.Remove(key))
            {
                throw new StateStorageNotFoundException(key);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEligibility : IStateEligibility
    {
        public StateEligibilityResult Check(StateOperationKind operation) => new(operation, []);
        public IStateBarrierHandle AcquireBarrier(
            object owner,
            string reasonCode,
            StateOperationMask operations = StateOperationMask.All,
            string? diagnostic = null,
            string? playerMessage = null) => throw new NotSupportedException();
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSessions : IGameSessionCoordinator
    {
        public event Action<GameSessionChanged>? SessionChanged { add { } remove { } }
        public int LoadCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NewGameAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadGameAsync(
            StateSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.CompletedTask;
        }
        public Task LoadGameAsync(
            StateRestorePlan plan,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.CompletedTask;
        }
        public Task ReturnToTitleAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RollbackAsync(
            StateSnapshot snapshot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHistory : IHistoryManager
    {
        public event Action<HistoryOpenStateChanged>? OpenStateChanged { add { } remove { } }
        public event Action<HistoryEntriesChanged>? EntriesChanged { add { } remove { } }
        public IReadOnlyList<HistoryEntry> Entries { get; } =
            [new(1, "line", "Alice", "Visible dialogue text", null)];
        public int Capacity => 10;
        public bool IsOpen => false;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Open() { }
        public void Close() { }
        public void Toggle() { }
        public void Clear() { }
        public Task<bool> ReplayVoiceAsync(
            long sequence,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeThumbnails : ISaveThumbnailProvider
    {
        private int next;
        public List<string> Deleted { get; } = [];
        public Task<SaveThumbnailResult> CaptureAsync(
            SaveSlotId slot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SaveThumbnailResult($"thumbnail-{++next}.png", null));
        public Task DeleteAsync(
            string logicalReference,
            CancellationToken cancellationToken = default)
        {
            Deleted.Add(logicalReference);
            return Task.CompletedTask;
        }
    }
}

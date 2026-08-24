using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DurableStateTests
{
    [Fact]
    public void EnvelopeSerializationIsCanonicalAndRoundTripsByteForByte()
    {
        using var payload = JsonDocument.Parse("{\"z\":2,\"a\":{\"y\":true,\"x\":1.0}}");
        var record = new StateRecord("game.example", StateScope.Game, 3, payload.RootElement);
        var capturedAt = new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.Zero);
        var snapshot = new StateSnapshot(1, capturedAt, [record]);
        var envelope = new StateEnvelope(
            1,
            "0.1.0",
            "build-42",
            "test/state",
            capturedAt.AddMinutes(1),
            StateScope.Game,
            snapshot,
            [new StateEnvelopeRecordMetadata(
                record.Key,
                record.SchemaVersion,
                StateContributorCapability.DurableRollback)],
            displayMetadata: new Dictionary<string, string>
            {
                ["speaker"] = "Maya",
                ["line"] = "line:42",
            });
        var serializer = new CanonicalJsonStateSerializer();

        var first = serializer.Serialize(envelope);
        var second = serializer.Serialize(envelope);
        var restored = serializer.Deserialize(first);
        var third = serializer.Serialize(restored);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(StateScope.Game, restored.Scope);
        Assert.Equal("build-42", restored.GameBuildId);
        Assert.Equal(2, restored.DisplayMetadata.Count);
        Assert.Equal("1.0", restored.Snapshot.Records[record.Key]
            .Payload.GetProperty("a").GetProperty("x").GetRawText());
    }

    [Fact]
    public void EnvelopeRejectsChecksumDamageAndUnknownFormats()
    {
        var serializer = new CanonicalJsonStateSerializer();
        var envelope = CreateEnvelope();
        var valid = serializer.Serialize(envelope);
        var damaged = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(valid).Replace("\"value\":1", "\"value\":2"));

        Assert.Throws<StateIntegrityException>(() => serializer.Deserialize(damaged));

        using var document = JsonDocument.Parse(valid);
        var oldPayload = document.RootElement.GetProperty("payload").GetRawText();
        var newPayload = oldPayload.Replace("\"formatVersion\":1", "\"formatVersion\":99");
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newPayload)))
            .ToLowerInvariant();
        var unsupported = Encoding.UTF8.GetBytes(
            $"{{\"checksum\":\"{checksum}\",\"payload\":{newPayload}}}");

        Assert.Throws<StateCompatibilityException>(() => serializer.Deserialize(unsupported));
    }

    [Fact]
    public async Task StoragePreservesOldCommitOnInterruptionAndPreCommitCancellation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var observer = new ControllableCommitObserver();
            var storage = new FileStateStorage(root, "test/state", observer);
            await storage.WriteAsync("manual/one", new byte[] { 1 });

            observer.FailBeforeCommit = true;
            await Assert.ThrowsAsync<StateStorageException>(() =>
                storage.WriteAsync("manual/one", new byte[] { 2 }));
            Assert.Equal(new byte[] { 1 }, await storage.ReadAsync("manual/one"));

            observer.FailBeforeCommit = false;
            using var cancellation = new CancellationTokenSource();
            observer.CancelBeforeCommit = cancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                storage.WriteAsync("manual/one", new byte[] { 3 }, cancellation.Token));
            Assert.Equal(new byte[] { 1 }, await storage.ReadAsync("manual/one"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StorageReturnsSuccessWhenCancellationRacesAfterCommit()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var observer = new ControllableCommitObserver
            {
                CancelAfterCommit = cancellation,
                FailAfterCommit = true,
            };
            var storage = new FileStateStorage(root, "test/state", observer);

            await storage.WriteAsync("quick/latest", new byte[] { 9, 8, 7 }, cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(new byte[] { 9, 8, 7 }, await storage.ReadAsync("quick/latest"));
            Assert.Equal(StateStorageCommitStage.Committed, storage.LastCommitStage);
            Assert.NotNull(storage.LastFailureMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StorageSerializesSameKeyAndNeverListsTemporaryFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var committedRoot = Path.Combine(root, "test", "state");
            Directory.CreateDirectory(committedRoot);
            var abandoned = Path.Combine(committedRoot, "orphan.state.tmp-crash");
            await File.WriteAllBytesAsync(abandoned, new byte[] { 4 });
            var storage = new FileStateStorage(root, "test/state");

            var writes = Enumerable.Range(0, 24).Select(index =>
                storage.WriteAsync(
                    "auto/rotating",
                    Enumerable.Repeat((byte)index, 4096).ToArray()));
            await Task.WhenAll(writes);

            var content = await storage.ReadAsync("auto/rotating");
            Assert.Equal(4096, content.Length);
            Assert.All(content, value => Assert.Equal(content[0], value));
            Assert.Equal(["auto/rotating"], await storage.ListAsync());
            Assert.False(File.Exists(abandoned));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadAsync("../escape"));
            Assert.Throws<ArgumentException>(() =>
                new FileStateStorage(root, "/absolute"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StateEnvelope CreateEnvelope()
    {
        var time = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var record = new StateRecord(
            "game.example",
            StateScope.Game,
            1,
            JsonSerializer.SerializeToElement(new { value = 1 }));
        return new StateEnvelope(
            1,
            "0.1.0",
            "test",
            "test/state",
            time,
            StateScope.Game,
            new StateSnapshot(1, time, [record]),
            [new StateEnvelopeRecordMetadata(
                record.Key,
                1,
                StateContributorCapability.DurableRollback)]);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"majo-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ControllableCommitObserver : IStateStorageCommitObserver
    {
        public bool FailBeforeCommit { get; set; }
        public CancellationTokenSource? CancelBeforeCommit { get; set; }
        public CancellationTokenSource? CancelAfterCommit { get; set; }
        public bool FailAfterCommit { get; set; }

        public ValueTask ObserveAsync(
            string key,
            StateStorageCommitStage stage,
            CancellationToken cancellationToken)
        {
            if (stage == StateStorageCommitStage.TemporaryFileFlushed)
            {
                if (FailBeforeCommit)
                {
                    throw new IOException("Simulated interrupted write.");
                }
                CancelBeforeCommit?.Cancel();
            }
            else
            {
                CancelAfterCommit?.Cancel();
                if (FailAfterCommit)
                {
                    throw new IOException("Post-commit observer failed.");
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}

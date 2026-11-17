using System.Text.Json;
using Majo.Runtime.Persistence.UnitTests.Testing;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.Persistence.UnitTests;

public sealed class StateMigrationTests
{
    [Fact]
    public void ResolvesRenamedRecordsInStableOrderWithoutMutatingSourceOrLiveState()
    {
        var source = Snapshot(Record("c.unknown"), Record("b.removed"), Record("a.old"));
        var migrations = new StateMigrationRegistry()
            .RegisterContributor(new DelegateStateContributorMigration("counter-v2", "a.old", 1,
                "game.counter", 2, payload => JsonSerializer.SerializeToElement(
                    new { value = payload.GetProperty("value").GetInt32() + 1 })))
            .RegisterRemovedContributor("b.removed");
        IStateContributor[] contributors =
        [
            new MetadataContributor("game.counter", schemaVersion: 2),
            new MetadataContributor("game.optional", capability: new StateContributorCapability(
                true, StateRollbackPolicy.Supported, StateRestoreRecordPolicy.UseDefault)),
        ];

        var result = migrations.ResolveRecords(source, contributors, StateScope.Game);

        var record = Assert.Single(result.Value).Value;
        Assert.Equal("game.counter", record.Key);
        Assert.Equal(2, record.SchemaVersion);
        Assert.Equal(6, record.Payload.GetProperty("value").GetInt32());
        Assert.Equal(["counter-v2", "removed:b.removed@1", "ignored:c.unknown@1"], result.Chain);
        Assert.Equal(3, source.Records.Count);
        Assert.Equal(1, source.Records["a.old"].SchemaVersion);
        Assert.Equal(5, source.Records["a.old"].Payload.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scope")]
    [InlineData("newer")]
    [InlineData("migration")]
    public void RejectsIncompatibleTargetsBeforeAnyLiveStateIsAccessed(string failure)
    {
        var snapshot = failure switch
        {
            "missing" => Snapshot(),
            "scope" => Snapshot(Record("game.counter", version: 2, scope: StateScope.Settings)),
            "newer" => Snapshot(Record("game.counter", version: 3)),
            _ => Snapshot(Record("game.counter")),
        };

        Assert.Throws<StateCompatibilityException>(() => new StateMigrationRegistry().ResolveRecords(
            snapshot, [new MetadataContributor("game.counter", schemaVersion: 2)], StateScope.Game));
    }

    [Fact]
    public void RejectsCollidingMigrationTargetsWithoutModifyingSourceRecords()
    {
        var source = Snapshot(Record("old.a"), Record("old.b"));
        var migrations = new StateMigrationRegistry();
        foreach (var key in new[] { "old.a", "old.b" })
            migrations.RegisterContributor(new DelegateStateContributorMigration(
                key, key, 1, "game.counter", 2, payload => payload));

        Assert.Throws<StateCompatibilityException>(() => migrations.ResolveRecords(
            source, [new MetadataContributor("game.counter", schemaVersion: 2)], StateScope.Game));
        Assert.Equal(["old.a", "old.b"], source.Records.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    private static StateRecord Record(string key, int version = 1, StateScope scope = StateScope.Game) =>
        new(key, scope, version, JsonSerializer.SerializeToElement(new { value = 5 }));

    private static StateSnapshot Snapshot(params StateRecord[] records) =>
        new(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, records);
}

using Majo.Runtime.State;

namespace Majo.Runtime.Persistence.UnitTests.Testing;

internal sealed class MetadataContributor(
    string key,
    IReadOnlyCollection<string>? dependencies = null,
    int schemaVersion = 1,
    StateContributorCapability? capability = null) : IStateContributor
{
    public string Key => key;
    public StateScope Scope => StateScope.Game;
    public int SchemaVersion => schemaVersion;
    public StateContributorCapability Capability => capability ?? StateContributorCapability.DurableRollback;
    public IReadOnlyCollection<string> Dependencies => dependencies ?? Array.Empty<string>();

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Metadata resolution must not capture live state.");

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Metadata resolution must not preflight live state.");

    public ValueTask ResetAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Metadata resolution must not reset live state.");
}

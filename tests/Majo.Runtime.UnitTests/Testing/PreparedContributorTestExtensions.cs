using Majo.Runtime.State;

namespace Majo.Runtime.UnitTests.Testing;

internal static class PreparedContributorTestExtensions
{
    public static async Task CommitPreparedAsync(
        this IPreparedStateContributor contributor,
        StateRecord record,
        CancellationToken cancellationToken = default)
    {
        var context = new RestoreContext(
            contributor.Scope,
            new Dictionary<string, StateRecord> { [contributor.Key] = record });
        await using var prepared = await contributor.PrepareRestoreAsync(
            record, context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        prepared.Commit();
        if (prepared is IStateRestorePresentation presentation)
            await presentation.WaitForPresentationAsync(cancellationToken);
        if (prepared is IStateRestoreActivation activation)
            await activation.ActivateAsync(cancellationToken);
    }
}

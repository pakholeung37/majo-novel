#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

/// <summary>A contributor capable of capturing a supplied durable script boundary.</summary>
public interface IScriptBoundaryStateContributor : IStateContributor
{
    StateRecord CaptureBoundary(ScriptCheckpoint checkpoint);
}

internal sealed class StateSnapshotCapture(IReadOnlyList<IStateContributor> contributors)
{
    public IStateContributor[] PersistentContributors(StateScope scope) =>
        contributors.Where(value => value.Scope == scope && value.Capability.SupportsPersistence).ToArray();

    public async Task<StateSnapshot> CaptureAsync(StateScope scope, LineBoundaryContext? boundary, CancellationToken token)
    {
        var records = new List<StateRecord>();
        foreach (var contributor in PersistentContributors(scope))
        {
            token.ThrowIfCancellationRequested();
            var record = boundary is not null && contributor is IScriptBoundaryStateContributor script
                ? script.CaptureBoundary(boundary.Script) : await contributor.CaptureAsync(token);
            StateEnvelopeHelper.ValidateRecord(contributor, record);
            records.Add(record);
        }
        return new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, records);
    }

    public StateSnapshot Reconcile(StateSnapshot snapshot, ScriptCheckpoint checkpoint)
    {
        var script = contributors.OfType<IScriptBoundaryStateContributor>().SingleOrDefault();
        if (script is null) return snapshot;
        var record = script.CaptureBoundary(checkpoint);
        StateEnvelopeHelper.ValidateRecord(script, record);
        return new StateSnapshot(snapshot.FormatVersion, snapshot.CapturedAt,
            snapshot.Records.Values.Where(value => value.Key != script.Key).Append(record));
    }
}

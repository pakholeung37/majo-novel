#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.State;

public interface IStateContributor
{
    string Key { get; }

    StateScope Scope { get; }

    int SchemaVersion { get; }

    IReadOnlyCollection<string> Dependencies => System.Array.Empty<string>();

    ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken);

    ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken);

    ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken);

    ValueTask ResetAsync(CancellationToken cancellationToken);
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Settings;

public interface IGlobalStateManager : IRuntimeService
{
    event Action<Exception>? PersistenceFailed;
    bool IsDirty { get; }
    string? LastPersistenceError { get; }
    ScopedStateRecoveryStatus RecoveryStatus { get; }
    void MarkDirty();
    Task FlushAsync(CancellationToken cancellationToken = default);
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Settings;

public interface ISettingsManager : IRuntimeService
{
    event Action<SettingsChanged>? Changed;
    event Action<Exception>? PersistenceFailed;

    RuntimeSettings Defaults { get; }
    RuntimeSettings Current { get; }
    bool IsDirty { get; }
    string? LastPersistenceError { get; }
    string? LastProjectionError { get; }
    ScopedStateRecoveryStatus RecoveryStatus { get; }

    void Update(RuntimeSettings settings);
    void ResetToDefaults();
    Task FlushAsync(CancellationToken cancellationToken = default);
}

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public interface IYarnHotReloadService : IRuntimeService
{
    event Action<YarnHotReloadStatusChanged>? StatusChanged;

    event Action<YarnHotReloadAttempt>? AttemptCompleted;

    YarnHotReloadStatus Status { get; }

    Task RequestReloadAsync(
        string? projectId = null,
        CancellationToken cancellationToken = default);
}

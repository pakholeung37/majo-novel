#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.State;

namespace Majo.Runtime.Settings;

public sealed class GlobalStateManager : ScopedStatePersistence, IGlobalStateManager
{
    public GlobalStateManager(
        IReadOnlyList<IStateContributor> contributors,
        IStateSerializer serializer,
        IStateStorage storage,
        StateMigrationRegistry migrations,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        TimeSpan debounce,
        bool strictRecovery = false,
        IRuntimeLogger? logger = null)
        : base(
            StateScope.Global,
            contributors,
            serializer,
            storage,
            migrations,
            runtimeVersion,
            gameBuildId,
            storageNamespace,
            "scopes/global",
            debounce,
            strictRecovery,
            logger)
    {
    }

    public new event Action<Exception>? PersistenceFailed
    {
        add => base.PersistenceFailed += value;
        remove => base.PersistenceFailed -= value;
    }

    public new void MarkDirty() => base.MarkDirty();
    public new Task FlushAsync(CancellationToken cancellationToken = default) =>
        base.FlushAsync(cancellationToken);
}

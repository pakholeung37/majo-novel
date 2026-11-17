#nullable enable

using System.Collections.Generic;

namespace Majo.Runtime.State;

internal static class StateEnvelopeHelper
{
    public static StateEnvelope CreateEnvelope(StateSnapshot snapshot, StateScope scope,
        IReadOnlyList<IStateContributor> contributors, string runtimeVersion, string gameBuildId,
        string storageNamespace, IReadOnlyDictionary<string, string>? displayMetadata = null) =>
        StateEnvelopeFactory.CreateEnvelope(snapshot, scope, contributors, runtimeVersion,
            gameBuildId, storageNamespace, displayMetadata);
    public static void ValidateSnapshotFormat(StateSnapshot snapshot) => StateEnvelopeFactory.ValidateSnapshotFormat(snapshot);
    public static void ValidateRecord(IStateContributor contributor, StateRecord record) => StateEnvelopeFactory.ValidateRecord(contributor, record);
    public static string FormatMigrationChain(IReadOnlyList<string> chain) => StateEnvelopeFactory.FormatMigrationChain(chain);
}

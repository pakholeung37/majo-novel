#nullable enable

namespace Majo.Runtime.Dialogue.Yarn;

public enum NodeEntryReason { Normal, Restore, InternalRebuild }

public sealed record NodeEntryRequest(
    string ProjectId,
    string NodeId,
    long Activation,
    NodeDirectionModule? Module,
    NodeEntryReason Reason);

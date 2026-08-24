namespace Majo.YarnBridge;

public sealed record YarnVmLineSnapshot(
    string NodeName,
    string LineId,
    int CapturedProgramCounter,
    string ExecutionState,
    int OperandStackDepth,
    int CallStackDepth,
    int PendingOptionCount,
    IReadOnlyList<string> ResolvedSubstitutions);

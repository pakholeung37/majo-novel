namespace Majo.YarnBridge;

public sealed record YarnVmLineSnapshot(
    string NodeName,
    string LineId,
    int CapturedProgramCounter,
    string ExecutionState,
    int OperandStackDepth,
    int CallStackDepth,
    int PendingOptionCount,
    IReadOnlyList<string> ResolvedSubstitutions)
{
    /// <summary>Saved bottom-to-top, matching the order used to rebuild the VM stacks.</summary>
    public IReadOnlyList<YarnVmCallFrame> CallStack { get; init; } = Array.Empty<YarnVmCallFrame>();
    public IReadOnlyList<YarnVmStackValue> OperandStack { get; init; } = Array.Empty<YarnVmStackValue>();
}

public sealed record YarnVmCallFrame(string NodeName, int InstructionIndex);
public sealed record YarnVmStackValue(string Type, string Value);

using System.Runtime.CompilerServices;
using System.Reflection;
using Yarn;

namespace Majo.YarnBridge;

public sealed class YarnVmStateBridge
{
    public static void ValidateRuntimeLayout()
    {
        ValidateField(typeof(Dialogue), "vm", typeof(VirtualMachine));
        ValidateField(typeof(VirtualMachine), "isContinuing", typeof(bool));
        ValidateField(
            typeof(VirtualMachine.State),
            "callStack",
            typeof(Stack<VirtualMachine.State.CallSite>));
    }

    public YarnVmLineSnapshot CaptureAtLine(
        Dialogue dialogue,
        string lineId,
        IReadOnlyList<string>? resolvedSubstitutions = null)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);

        ref var vm = ref YarnPrivateAccess.GetVirtualMachine(dialogue);
        var delivered = EnsureSafeLineBoundary(vm, lineId);
        resolvedSubstitutions ??= Array.Empty<string>();
        if (resolvedSubstitutions.Count != delivered.RunLine.SubstitutionCount)
        {
            throw new InvalidOperationException(
                $"Line '{lineId}' requires {delivered.RunLine.SubstitutionCount} substitutions, " +
                $"but {resolvedSubstitutions.Count} were provided.");
        }

        ref var callStack = ref YarnPrivateAccess.GetCallStack(vm.state);
        return new YarnVmLineSnapshot(
            vm.state.currentNodeName!,
            lineId,
            vm.state.programCounter,
            vm.CurrentExecutionState.ToString(),
            vm.state.stack.Count,
            callStack.Count,
            vm.state.currentOptions.Count,
            Array.AsReadOnly(resolvedSubstitutions.ToArray()));
    }

    public void PrepareReplay(
        Dialogue dialogue,
        Program program,
        YarnVmLineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(snapshot);

        var anchor = FindUniqueLine(program, snapshot.LineId);
        dialogue.SetProgram(program);
        dialogue.SetNode(anchor.NodeName);
        InjectReplayAfterNodeSelected(dialogue, program, snapshot);
    }

    public void InjectReplayAfterNodeSelected(
        Dialogue dialogue,
        Program program,
        YarnVmLineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.OperandStackDepth != 0 ||
            snapshot.CallStackDepth != 0 ||
            snapshot.PendingOptionCount != 0)
        {
            throw new NotSupportedException(
                "The proof-of-concept bridge only restores top-level line boundaries with empty VM stacks and no pending options.");
        }

        var anchor = FindUniqueLine(program, snapshot.LineId);
        var instruction = anchor.Node.Instructions[anchor.InstructionIndex];
        if (instruction.RunLine.SubstitutionCount != snapshot.ResolvedSubstitutions.Count)
        {
            throw new InvalidOperationException(
                $"Line '{snapshot.LineId}' now requires " +
                $"{instruction.RunLine.SubstitutionCount} substitutions, but the checkpoint " +
                $"contains {snapshot.ResolvedSubstitutions.Count}.");
        }

        ref var vm = ref YarnPrivateAccess.GetVirtualMachine(dialogue);
        if (YarnPrivateAccess.GetIsContinuing(vm))
        {
            throw new InvalidOperationException(
                "Yarn VM state cannot be restored while Continue is executing.");
        }

        if (vm.currentNode is null ||
            !string.Equals(
                vm.state.currentNodeName,
                anchor.NodeName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Yarn VM must select node '{anchor.NodeName}' before replay state is injected.");
        }

        vm.state.ClearStack();
        vm.state.currentOptions.Clear();
        ref var callStack = ref YarnPrivateAccess.GetCallStack(vm.state);
        callStack.Clear();
        foreach (var substitution in snapshot.ResolvedSubstitutions)
        {
            vm.state.PushValue(new Value(substitution));
        }

        vm.state.currentNodeName = anchor.NodeName;
        vm.state.programCounter = anchor.InstructionIndex;
        vm.currentNode = anchor.Node;
        vm._executionState = VirtualMachine.ExecutionState.WaitingForContinue;
    }

    private static Instruction EnsureSafeLineBoundary(VirtualMachine vm, string lineId)
    {
        if (YarnPrivateAccess.GetIsContinuing(vm))
        {
            throw new InvalidOperationException(
                "Yarn VM state cannot be captured while Continue is executing.");
        }

        if (vm.CurrentExecutionState != VirtualMachine.ExecutionState.WaitingForContinue)
        {
            throw new InvalidOperationException(
                $"Expected Yarn VM to be waiting for continue, but it was {vm.CurrentExecutionState}.");
        }

        if (vm.currentNode is null || string.IsNullOrEmpty(vm.state.currentNodeName))
        {
            throw new InvalidOperationException("Yarn VM has no current node.");
        }

        var deliveredInstructionIndex = vm.state.programCounter - 1;
        if (deliveredInstructionIndex < 0 ||
            deliveredInstructionIndex >= vm.currentNode.Instructions.Count)
        {
            throw new InvalidOperationException(
                "Yarn VM program counter does not point after a delivered line.");
        }

        var delivered = vm.currentNode.Instructions[deliveredInstructionIndex];
        if (delivered.InstructionTypeCase !=
                Instruction.InstructionTypeOneofCase.RunLine ||
            !string.Equals(delivered.RunLine.LineID, lineId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Yarn VM is not waiting after line '{lineId}'.");
        }

        return delivered;
    }

    private static LineAnchor FindUniqueLine(Program program, string lineId)
    {
        LineAnchor? match = null;
        foreach (var pair in program.Nodes)
        {
            for (var index = 0; index < pair.Value.Instructions.Count; index++)
            {
                var instruction = pair.Value.Instructions[index];
                if (instruction.InstructionTypeCase !=
                        Instruction.InstructionTypeOneofCase.RunLine ||
                    !string.Equals(
                        instruction.RunLine.LineID,
                        lineId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (match is not null)
                {
                    throw new InvalidOperationException(
                        $"Line ID '{lineId}' maps to more than one Yarn instruction.");
                }

                match = new LineAnchor(pair.Key, pair.Value, index);
            }
        }

        return match ?? throw new InvalidOperationException(
            $"Line ID '{lineId}' was not found in the Yarn program.");
    }

    private sealed record LineAnchor(
        string NodeName,
        Node Node,
        int InstructionIndex);

    private static void ValidateField(Type owner, string name, Type expectedType)
    {
        var field = owner.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null || field.FieldType != expectedType)
        {
            throw new InvalidOperationException(
                $"Yarn Spinner runtime layout is incompatible: expected field " +
                $"'{owner.FullName}.{name}' of type '{expectedType.FullName}'.");
        }
    }

    private static class YarnPrivateAccess
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "vm")]
        internal static extern ref VirtualMachine GetVirtualMachine(Dialogue dialogue);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "callStack")]
        internal static extern ref Stack<VirtualMachine.State.CallSite> GetCallStack(
            VirtualMachine.State state);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "isContinuing")]
        internal static extern ref bool GetIsContinuingReference(VirtualMachine vm);

        internal static bool GetIsContinuing(VirtualMachine vm) =>
            GetIsContinuingReference(vm);
    }
}

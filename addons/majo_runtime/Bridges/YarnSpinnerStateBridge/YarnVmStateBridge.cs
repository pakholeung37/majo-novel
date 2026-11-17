using System.Runtime.CompilerServices;
using System.Reflection;
using System.Globalization;
using Yarn;

namespace Majo.YarnBridge;

public sealed class YarnVmStateBridge
{
    public int GetCallStackDepth(Dialogue dialogue) =>
        YarnPrivateAccess.GetCallStack(YarnPrivateAccess.GetVirtualMachine(dialogue).state).Count;

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
        IReadOnlyList<string>? resolvedSubstitutions = null) =>
        CaptureLine(dialogue, lineId, resolvedSubstitutions, allowDelivery: false);

    public YarnVmLineSnapshot CaptureAtLineEntry(
        Dialogue dialogue,
        string lineId,
        IReadOnlyList<string>? resolvedSubstitutions = null) =>
        CaptureLine(dialogue, lineId, resolvedSubstitutions, allowDelivery: true);

    private static YarnVmLineSnapshot CaptureLine(
        Dialogue dialogue,
        string lineId,
        IReadOnlyList<string>? resolvedSubstitutions,
        bool allowDelivery)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);

        ref var vm = ref YarnPrivateAccess.GetVirtualMachine(dialogue);
        var delivered = EnsureSafeLineBoundary(vm, lineId, allowDelivery);
        resolvedSubstitutions ??= Array.Empty<string>();
        if (resolvedSubstitutions.Count != delivered.RunLine.SubstitutionCount)
        {
            throw new InvalidOperationException(
                $"Line '{lineId}' requires {delivered.RunLine.SubstitutionCount} substitutions, " +
                $"but {resolvedSubstitutions.Count} were provided.");
        }

        ref var callStack = ref YarnPrivateAccess.GetCallStack(vm.state);
        if (vm.state.currentOptions.Count != 0)
        {
            throw new NotSupportedException(
                "Yarn VM line checkpoints cannot contain pending options.");
        }

        return new YarnVmLineSnapshot(
            vm.state.currentNodeName!,
            lineId,
            vm.state.programCounter + (YarnPrivateAccess.GetIsContinuing(vm) ? 1 : 0),
            VirtualMachine.ExecutionState.WaitingForContinue.ToString(),
            vm.state.stack.Count,
            callStack.Count,
            vm.state.currentOptions.Count,
            Array.AsReadOnly(resolvedSubstitutions.ToArray()))
        {
            CallStack = Array.AsReadOnly(callStack.Reverse()
                .Select(frame => new YarnVmCallFrame(frame.nodeName, frame.instruction)).ToArray()),
            OperandStack = Array.AsReadOnly(vm.state.stack.Reverse().Select(CaptureValue).ToArray()),
        };
    }

    public void PrepareReplay(
        Dialogue dialogue,
        Program program,
        YarnVmLineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(snapshot);

        ValidateReplay(program, snapshot);
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

        ValidateReplay(program, snapshot);
        var anchor = FindUniqueLine(program, snapshot.LineId);

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
        foreach (var frame in snapshot.CallStack)
            callStack.Push(new VirtualMachine.State.CallSite { nodeName = frame.NodeName, instruction = frame.InstructionIndex });
        foreach (var value in snapshot.OperandStack)
            vm.state.PushValue(RestoreValue(value));
        foreach (var substitution in snapshot.ResolvedSubstitutions)
        {
            vm.state.PushValue(new Value(substitution));
        }

        vm.state.currentNodeName = anchor.NodeName;
        vm.state.programCounter = anchor.InstructionIndex;
        vm.currentNode = anchor.Node;
        vm._executionState = VirtualMachine.ExecutionState.WaitingForContinue;
    }

    public void ValidateReplay(Program program, YarnVmLineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(snapshot);
        var anchor = FindUniqueLine(program, snapshot.LineId);
        if (!string.Equals(anchor.NodeName, snapshot.NodeName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Line '{snapshot.LineId}' does not belong to node '{snapshot.NodeName}'.");
        if (snapshot.OperandStackDepth != snapshot.OperandStack.Count ||
            snapshot.CallStackDepth != snapshot.CallStack.Count || snapshot.PendingOptionCount != 0)
            throw new InvalidOperationException("Checkpoint VM stack depths do not match their payloads, or options are pending.");
        if (anchor.Node.Instructions[anchor.InstructionIndex].RunLine.SubstitutionCount != snapshot.ResolvedSubstitutions.Count)
            throw new InvalidOperationException($"Line '{snapshot.LineId}' substitution count does not match its checkpoint.");
        foreach (var value in snapshot.OperandStack) _ = RestoreValue(value);
        foreach (var frame in snapshot.CallStack)
        {
            if (!program.Nodes.TryGetValue(frame.NodeName, out var caller) ||
                frame.InstructionIndex < 0 || frame.InstructionIndex >= caller.Instructions.Count ||
                caller.Instructions[frame.InstructionIndex].InstructionTypeCase is not
                    (Instruction.InstructionTypeOneofCase.DetourToNode or Instruction.InstructionTypeOneofCase.PeekAndDetourToNode))
                throw new InvalidOperationException($"Invalid checkpoint return address '{frame.NodeName}:{frame.InstructionIndex}'.");
        }
    }

    private static YarnVmStackValue CaptureValue(Value value) => value.Type switch
    {
        var type when type == Types.String => new("string", value.ConvertTo<string>()),
        var type when type == Types.Number => new("number", value.ConvertTo<float>().ToString("R", CultureInfo.InvariantCulture)),
        var type when type == Types.Boolean => new("bool", value.ConvertTo<bool>() ? "true" : "false"),
        _ => throw new NotSupportedException($"Unsupported Yarn operand type '{value.Type}'."),
    };

    private static Value RestoreValue(YarnVmStackValue value) => value.Type switch
    {
        "string" => new Value(value.Value),
        "number" => new Value(float.Parse(value.Value, CultureInfo.InvariantCulture)),
        "bool" => new Value(bool.Parse(value.Value)),
        _ => throw new InvalidOperationException($"Unsupported checkpoint operand type '{value.Type}'."),
    };

    private static Instruction EnsureSafeLineBoundary(VirtualMachine vm, string lineId, bool allowDelivery)
    {
        var delivering = allowDelivery && YarnPrivateAccess.GetIsContinuing(vm) &&
            vm.CurrentExecutionState == VirtualMachine.ExecutionState.DeliveringContent;
        if (YarnPrivateAccess.GetIsContinuing(vm) && !delivering)
        {
            throw new InvalidOperationException(
                "Yarn VM state cannot be captured while Continue is executing.");
        }

        if (!delivering && vm.CurrentExecutionState != VirtualMachine.ExecutionState.WaitingForContinue)
        {
            throw new InvalidOperationException(
                $"Expected Yarn VM to be waiting for continue, but it was {vm.CurrentExecutionState}.");
        }

        if (vm.currentNode is null || string.IsNullOrEmpty(vm.state.currentNodeName))
        {
            throw new InvalidOperationException("Yarn VM has no current node.");
        }

        var deliveredInstructionIndex = vm.state.programCounter - (delivering ? 0 : 1);
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

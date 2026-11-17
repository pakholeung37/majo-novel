using Yarn;

namespace Majo.YarnBridge;

/// <summary>Adapts compiled programs without changing Yarn sources or the Godot importer.</summary>
public static class YarnProgramBridge
{
    public const string NodeEntryCommand = "__majo_node_entry";

    public static Program WithNodeEntries(Program program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var prepared = program.Clone();
        var smartVariables = program.SmartVariableNodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in prepared.Nodes)
        {
            var node = pair.Value;
            if (smartVariables.Contains(pair.Key) || HasNodeEntry(node))
                continue;

            foreach (var instruction in node.Instructions)
            {
                switch (instruction.InstructionTypeCase)
                {
                    case Instruction.InstructionTypeOneofCase.JumpTo:
                        instruction.JumpTo.Destination++;
                        break;
                    case Instruction.InstructionTypeOneofCase.JumpIfFalse:
                        instruction.JumpIfFalse.Destination++;
                        break;
                    case Instruction.InstructionTypeOneofCase.AddOption:
                        instruction.AddOption.Destination++;
                        break;
                    case Instruction.InstructionTypeOneofCase.AddSaliencyCandidate:
                        instruction.AddSaliencyCandidate.Destination++;
                        break;
                    case Instruction.InstructionTypeOneofCase.AddSaliencyCandidateFromNode:
                        instruction.AddSaliencyCandidateFromNode.Destination++;
                        break;
                }
            }
            node.Instructions.Insert(0, new Instruction
            {
                RunCommand = new Instruction.Types.RunCommandInstruction { CommandText = NodeEntryCommand },
            });
        }
        return prepared;
    }

    internal static bool HasNodeEntry(Node node) =>
        node.Instructions.Count > 0 &&
        node.Instructions[0].RunCommand is { SubstitutionCount: 0, CommandText: NodeEntryCommand };
}

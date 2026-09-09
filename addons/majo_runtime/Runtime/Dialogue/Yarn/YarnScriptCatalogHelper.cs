#nullable enable

using System;
using System.Linq;
using System.Security.Cryptography;
using Google.Protobuf;
using Majo.Runtime.Script;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

internal static class YarnScriptCatalogHelper
{
    public static ScriptDescriptor CreateDescriptor(YarnProject project, ScriptId id)
    {
        if (project.Program is null)
        {
            throw new ScriptRuntimeException(
                $"Script project '{id.ProjectId}' has no compiled program.");
        }

        if (!project.Program.Nodes.TryGetValue(id.NodeName, out var node))
        {
            throw new ScriptRuntimeException(
                $"Node '{id.NodeName}' was not found in project '{id.ProjectId}'.");
        }

        var allLineIds = project.Program.Nodes.Values
            .SelectMany(programNode => programNode.Instructions)
            .Where(instruction => instruction.InstructionTypeCase ==
                global::Yarn.Instruction.InstructionTypeOneofCase.RunLine)
            .Select(instruction => instruction.RunLine.LineID)
            .ToArray();
        var duplicate = allLineIds.GroupBy(lineId => lineId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ScriptCheckpointException(
                $"Project '{id.ProjectId}' contains duplicate line ID '{duplicate.Key}'.");
        }

        var lineIds = node.Instructions
            .Where(instruction => instruction.InstructionTypeCase ==
                global::Yarn.Instruction.InstructionTypeOneofCase.RunLine)
            .Select(instruction => instruction.RunLine.LineID)
            .ToArray();
        var lineSubstitutionCounts = node.Instructions
            .Where(instruction => instruction.InstructionTypeCase ==
                global::Yarn.Instruction.InstructionTypeOneofCase.RunLine)
            .ToDictionary(
                instruction => instruction.RunLine.LineID,
                instruction => instruction.RunLine.SubstitutionCount,
                StringComparer.Ordinal);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(project.Program.ToByteArray())).ToLowerInvariant();
        return new ScriptDescriptor(id, fingerprint, lineIds, lineSubstitutionCounts);
    }
}

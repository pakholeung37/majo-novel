#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

internal static class YarnHotReloadComparison
{
    public static bool IsContentEquivalent(YarnProjectVersion active, YarnProjectVersion candidate)
    {
        if (!string.Equals(active.ProgramFingerprint, candidate.ProgramFingerprint, StringComparison.Ordinal) ||
            !active.NodeNames.SetEquals(candidate.NodeNames) ||
            !active.LineIds.SetEquals(candidate.LineIds) ||
            !active.ImplicitLineIds.SetEquals(candidate.ImplicitLineIds) ||
            !DirectionModulesEquivalent(active.DirectionModules, candidate.DirectionModules))
        {
            return false;
        }

        if (active.LineNodes.Count != candidate.LineNodes.Count ||
            candidate.LineNodes.Any(pair => !active.LineNodes.TryGetValue(pair.Key, out var node) ||
                !string.Equals(node, pair.Value, StringComparison.Ordinal)))
        {
            return false;
        }

        var activeLoc = active.Project.baseLocalization;
        var candidateLoc = candidate.Project.baseLocalization;
        return candidate.LineIds.All(lineId => string.Equals(
            activeLoc?.GetLocalizedString(lineId),
            candidateLoc?.GetLocalizedString(lineId),
            StringComparison.Ordinal));
    }

    private static bool DirectionModulesEquivalent(
        IReadOnlyList<NodeDirectionModule> active,
        IReadOnlyList<NodeDirectionModule> candidate)
    {
        if (active.Count != candidate.Count)
        {
            return false;
        }

        for (var index = 0; index < active.Count; index++)
        {
            var left = active[index];
            var right = candidate[index];
            if (!string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal) ||
                !left.Symbols.Stages.SetEquals(right.Symbols.Stages) ||
                !left.Symbols.Views.SetEquals(right.Symbols.Views) ||
                !left.Symbols.ShotAliases.SetEquals(right.Symbols.ShotAliases) ||
                !left.Symbols.Marks.SetEquals(right.Symbols.Marks) ||
                !left.Resources.SequenceEqual(right.Resources) ||
                !left.SourceMaps.SequenceEqual(right.SourceMaps) ||
                !left.Imports.SequenceEqual(right.Imports) ||
                !left.MarkDefinitions.SequenceEqual(right.MarkDefinitions) ||
                !left.Diagnostics.SequenceEqual(right.Diagnostics))
            {
                return false;
            }
        }

        return true;
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Actors.Layered;

public sealed class LayerCompositionResolver
{
    private const int MaximumAliasDepth = 64;
    private readonly Dictionary<string, string> aliases;
    private readonly HashSet<string> layerPaths;
    private readonly HashSet<string> groupPaths;
    private readonly string[] orderedLayerPaths;
    private readonly HashSet<string> initialLayers;

    public LayerCompositionResolver(LayeredCharacterManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        aliases = manifest.CompositionMap;
        orderedLayerPaths = manifest.Layers
            .OrderBy(layer => layer.SortingOrder)
            .ThenBy(layer => layer.Path, StringComparer.Ordinal)
            .Select(layer => layer.Path)
            .ToArray();
        layerPaths = new HashSet<string>(orderedLayerPaths, StringComparer.Ordinal);
        initialLayers = manifest.Layers
            .Where(layer => layer.Enabled)
            .Select(layer => layer.Path)
            .ToHashSet(StringComparer.Ordinal);
        groupPaths = [];
        foreach (var path in orderedLayerPaths)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                groupPaths.Add(path[..slash]);
                slash = path.IndexOf('/', slash + 1);
            }
        }

        DefaultAppearance = manifest.DefaultAppearance;
    }

    public string DefaultAppearance { get; }

    public HashSet<string> CreateDefaultState()
    {
        var state = new HashSet<string>(initialLayers, StringComparer.Ordinal);
        Apply(state, DefaultAppearance);
        return state;
    }

    public void Apply(ISet<string> state, string appearance)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        ApplyComposition(state, appearance, [], 0);
    }

    public string Canonicalize(IEnumerable<string> activeLayers)
    {
        ArgumentNullException.ThrowIfNull(activeLayers);
        var active = activeLayers.ToHashSet(StringComparer.Ordinal);
        var expressions = new List<string> { "-" };
        foreach (var path in orderedLayerPaths.Where(active.Contains))
        {
            var slash = path.LastIndexOf('/');
            expressions.Add(slash < 0
                ? $"+{path}"
                : $"{path[..slash]}+{path[(slash + 1)..]}");
        }

        return string.Join(',', expressions);
    }

    private void ApplyComposition(
        ISet<string> state,
        string composition,
        HashSet<string> aliasStack,
        int depth)
    {
        if (depth > MaximumAliasDepth)
        {
            throw new ActorException("Layered character appearance alias nesting is too deep.");
        }

        foreach (var rawExpression in composition.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var expression = rawExpression.Trim();
            if (expression.Length == 0)
            {
                continue;
            }

            if (aliases.TryGetValue(expression, out var expansion))
            {
                if (!aliasStack.Add(expression))
                {
                    throw new ActorException(
                        $"Layered character appearance alias cycle contains '{expression}'.");
                }

                ApplyComposition(state, expansion, aliasStack, depth + 1);
                aliasStack.Remove(expression);
                continue;
            }

            ApplyExpression(state, expression);
        }
    }

    private void ApplyExpression(ISet<string> state, string expression)
    {
        if (expression is "+" or "-")
        {
            if (expression == "+")
            {
                state.UnionWith(layerPaths);
            }
            else
            {
                state.Clear();
            }

            return;
        }

        var parsed = ParseExpression(expression);
        switch (parsed.Operation)
        {
            case '>':
                if (parsed.Member.Length > 0)
                {
                    DisableNode(state, parsed.Group);
                    EnableNode(state, Combine(parsed.Group, parsed.Member));
                }
                else
                {
                    SelectGroup(state, parsed.Group);
                }
                break;
            case '+':
                EnableNode(state, Combine(parsed.Group, parsed.Member));
                break;
            case '-':
                DisableNode(state, Combine(parsed.Group, parsed.Member));
                break;
            default:
                throw new ActorException(
                    $"Unsupported layered character expression '{expression}'.");
        }
    }

    private ParsedExpression ParseExpression(string expression)
    {
        if (expression[0] is '+' or '-')
        {
            return new ParsedExpression(string.Empty, expression[0], expression[1..]);
        }

        if (expression[^1] is '+' or '-' or '>' or '~')
        {
            return new ParsedExpression(
                expression[..^1],
                NormalizeOperation(expression[^1]),
                string.Empty);
        }

        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] is not ('+' or '-' or '>' or '~'))
            {
                continue;
            }

            var group = expression[..index];
            var member = expression[(index + 1)..];
            if (groupPaths.Contains(group) && NodeExists(Combine(group, member)))
            {
                return new ParsedExpression(
                    group,
                    NormalizeOperation(expression[index]),
                    member);
            }
        }

        throw new ActorException($"Invalid layered character expression '{expression}'.");
    }

    private void SelectGroup(ISet<string> state, string group)
    {
        if (!groupPaths.Contains(group))
        {
            throw new ActorException($"Layered character group '{group}' does not exist.");
        }

        var slash = group.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : group[..slash];
        foreach (var path in layerPaths.Where(path => IsDirectOrNestedChild(parent, path)).ToArray())
        {
            state.Remove(path);
        }

        EnableNode(state, group);
    }

    private void EnableNode(ISet<string> state, string node)
    {
        if (layerPaths.Contains(node))
        {
            state.Add(node);
            return;
        }

        if (!groupPaths.Contains(node))
        {
            throw new ActorException($"Layered character layer or group '{node}' does not exist.");
        }

        foreach (var path in layerPaths.Where(path => IsDirectOrNestedChild(node, path)))
        {
            state.Add(path);
        }
    }

    private void DisableNode(ISet<string> state, string node)
    {
        if (layerPaths.Contains(node))
        {
            state.Remove(node);
            return;
        }

        if (!groupPaths.Contains(node))
        {
            throw new ActorException($"Layered character layer or group '{node}' does not exist.");
        }

        foreach (var path in state.Where(path => IsDirectOrNestedChild(node, path)).ToArray())
        {
            state.Remove(path);
        }
    }

    private bool NodeExists(string node) =>
        layerPaths.Contains(node) || groupPaths.Contains(node);

    private static string Combine(string group, string member) =>
        group.Length == 0 ? member : member.Length == 0 ? group : $"{group}/{member}";

    private static bool IsDirectOrNestedChild(string group, string path) =>
        group.Length == 0 || path.StartsWith($"{group}/", StringComparison.Ordinal);

    private static char NormalizeOperation(char operation) => operation == '~' ? '>' : operation;

    private readonly record struct ParsedExpression(string Group, char Operation, string Member);
}

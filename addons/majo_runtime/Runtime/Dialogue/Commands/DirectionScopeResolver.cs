#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Dialogue.Yarn;

namespace Majo.Runtime.Dialogue.Commands;

public interface IDirectionScopeResolver : IRuntimeService
{
    StageId ResolveStage(string nodeId, string symbol);

    ViewId ResolveView(string nodeId, string symbol);

    void ImportStage(string nodeId, string aliasName, string sourceNodeId, string sourceSymbol);

    void ImportView(string nodeId, string aliasName, string sourceNodeId, string sourceSymbol);

    void DefineShot(string nodeId, string shotAlias, ViewPatch patch);

    ViewPatch ResolveShot(string nodeId, string shotAlias);

    void ApplyModule(NodeDirectionModule directionModule);
    void EnterNode(string nodeId);
    void ExitNode(string nodeId);
    void DefineMark(string nodeId, string name, DirectionPoint position);
    DirectionPoint ResolveMark(string nodeId, string name);
}

/// <summary>
/// Resolves unqualified authoring symbols into stable session identifiers. The
/// compiler-facing import methods deliberately exist before import syntax does,
/// so Yarn commands never need a temporary runtime import command.
/// </summary>
public sealed class DirectionScopeResolver : IDirectionScopeResolver
{
    private readonly Dictionary<(string Node, string Alias), StageId> stageImports = new();
    private readonly Dictionary<(string Node, string Alias), ViewId> viewImports = new();
    private readonly Dictionary<(string Node, string Alias), ViewPatch> shotAliases = new();
    private readonly Dictionary<(string Node, string Name), DirectionPoint> marks = new();
    private string? activeNode;
    private NodeDirectionModule? activeModule;

    public StageId ResolveStage(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        if (string.Equals(local, StageId.Main.Value, StringComparison.Ordinal))
            return StageId.Main;
        if (stageImports.TryGetValue((node, local), out var imported))
            return imported;
        EnsureKnownSymbol(node, local, isStage: true);
        return new StageId(Qualify(node, "stages", local));
    }

    public ViewId ResolveView(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        if (string.Equals(local, ViewId.Main.Value, StringComparison.Ordinal))
            return ViewId.Main;
        if (viewImports.TryGetValue((node, local), out var imported))
            return imported;
        EnsureKnownSymbol(node, local, isStage: false);
        return new ViewId(Qualify(node, "views", local));
    }

    public void ImportStage(
        string nodeId,
        string aliasName,
        string sourceNodeId,
        string sourceSymbol)
    {
        var (node, localAlias) = Validate(nodeId, aliasName);
        var source = ResolveLocalStage(sourceNodeId, sourceSymbol);
        if (stageImports.TryGetValue((node, localAlias), out var existing))
        {
            if (existing == source) return;
            throw Duplicate("Stage", node, localAlias);
        }
        AddImport(stageImports, (node, localAlias), source, "Stage");
    }

    public void ImportView(
        string nodeId,
        string aliasName,
        string sourceNodeId,
        string sourceSymbol)
    {
        var (node, localAlias) = Validate(nodeId, aliasName);
        var source = ResolveLocalView(sourceNodeId, sourceSymbol);
        if (viewImports.TryGetValue((node, localAlias), out var existing))
        {
            if (existing == source) return;
            throw Duplicate("View", node, localAlias);
        }
        AddImport(viewImports, (node, localAlias), source, "View");
    }

    public void DefineShot(string nodeId, string shotAlias, ViewPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var (node, localAlias) = Validate(nodeId, shotAlias);
        AddImport(shotAliases, (node, localAlias), patch, "Shot");
    }

    public ViewPatch ResolveShot(string nodeId, string shotAlias)
    {
        var (node, localAlias) = Validate(nodeId, shotAlias);
        if (!shotAliases.TryGetValue((node, localAlias), out var patch))
            throw new DirectionReferenceException(
                $"Shot alias '{localAlias}' is not defined in Node '{node}'.");
        return patch;
    }

    public void DefineMark(string nodeId, string name, DirectionPoint position)
    {
        var (node, local) = Validate(nodeId, name);
        position.Validate();
        if (marks.TryGetValue((node, local), out var existing))
        {
            if (existing == position) return;
            throw Duplicate("Mark", node, local);
        }
        AddImport(marks, (node, local), position, "Mark");
    }

    public DirectionPoint ResolveMark(string nodeId, string name)
    {
        var (node, local) = Validate(nodeId, name);
        if (!marks.TryGetValue((node, local), out var position))
            throw new DirectionReferenceException($"Mark '{local}' is not defined in Node '{node}'.");
        return position;
    }

    public void ApplyModule(NodeDirectionModule directionModule)
    {
        ArgumentNullException.ThrowIfNull(directionModule);
        var error = directionModule.Diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        if (error is not null)
            throw new DirectionReferenceException(
                $"Direction authoring error in Node '{directionModule.NodeId}' at " +
                $"{error.File}:{error.StartLine}:{error.StartColumn}: {error.Message}");
        if (string.Equals(activeNode, directionModule.NodeId, StringComparison.Ordinal))
            ExitNode(directionModule.NodeId);
        EnterNode(directionModule.NodeId);
        foreach (var import in directionModule.Imports)
        {
            if (import.Kind.Equals("stage", StringComparison.OrdinalIgnoreCase))
                ImportStage(directionModule.NodeId, import.Alias, import.SourceNode, import.SourceSymbol);
            else if (import.Kind.Equals("view", StringComparison.OrdinalIgnoreCase))
                ImportView(directionModule.NodeId, import.Alias, import.SourceNode, import.SourceSymbol);
        }
        foreach (var mark in directionModule.MarkDefinitions)
            DefineMark(directionModule.NodeId, mark.Name, mark.Position);
        activeModule = directionModule;
    }

    public void EnterNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var normalized = nodeId.Trim();
        if (activeNode is not null && !activeNode.Equals(normalized, StringComparison.Ordinal))
            ExitNode(activeNode);
        activeNode = normalized;
        activeModule = null;
    }

    public void ExitNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) ||
            !string.Equals(activeNode, nodeId.Trim(), StringComparison.Ordinal)) return;
        RemoveNode(stageImports); RemoveNode(viewImports); RemoveNode(shotAliases);
        foreach (var key in marks.Keys.Where(key => key.Node == activeNode).ToArray()) marks.Remove(key);
        activeNode = null;
        activeModule = null;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stageImports.Clear();
        viewImports.Clear();
        shotAliases.Clear();
        marks.Clear();
        activeNode = null;
        activeModule = null;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => ResetAsync(cancellationToken);

    private static StageId ResolveLocalStage(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        return string.Equals(local, StageId.Main.Value, StringComparison.Ordinal)
            ? StageId.Main
            : new StageId(Qualify(node, "stages", local));
    }

    private static ViewId ResolveLocalView(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        return string.Equals(local, ViewId.Main.Value, StringComparison.Ordinal)
            ? ViewId.Main
            : new ViewId(Qualify(node, "views", local));
    }

    private static (string Node, string Symbol) Validate(string nodeId, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return (nodeId.Trim(), symbol.Trim());
    }

    private static string Qualify(string node, string kind, string symbol) =>
        $"{Escape(node)}/{kind}/{Escape(symbol)}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private void EnsureKnownSymbol(string node, string symbol, bool isStage)
    {
        if (activeModule is null || !string.Equals(activeNode, node, StringComparison.Ordinal))
            return;

        var known = isStage
            ? activeModule.Symbols.Stages.Contains(symbol) || activeModule.Imports.Any(import =>
                import.Kind.Equals("stage", StringComparison.OrdinalIgnoreCase) &&
                import.Alias.Equals(symbol, StringComparison.Ordinal))
            : activeModule.Symbols.Views.Contains(symbol) || activeModule.Imports.Any(import =>
                import.Kind.Equals("view", StringComparison.OrdinalIgnoreCase) &&
                import.Alias.Equals(symbol, StringComparison.Ordinal));
        if (!known)
        {
            throw new DirectionReferenceException(
                $"{(isStage ? "Stage" : "View")} symbol '{symbol}' is not defined in Node '{node}'.");
        }
    }

    private static void AddImport<T>(
        IDictionary<(string Node, string Alias), T> imports,
        (string Node, string Alias) key,
        T value,
        string kind)
    {
        if (!imports.TryAdd(key, value))
        {
            throw Duplicate(kind, key.Node, key.Alias);
        }
    }

    private static DirectionReferenceException Duplicate(string kind, string node, string alias) =>
        new($"{kind} import alias '{alias}' is already defined in Node '{node}'.");

    private void RemoveNode<T>(IDictionary<(string Node, string Alias), T> values)
    {
        if (activeNode is null) return;
        foreach (var key in values.Keys.Where(key => key.Node == activeNode).ToArray())
            values.Remove(key);
    }
}

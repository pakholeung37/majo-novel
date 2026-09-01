#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Dialogue.Commands;

public interface IDirectionScopeResolver : IRuntimeService
{
    StageId ResolveStage(string nodeId, string symbol);

    ViewId ResolveView(string nodeId, string symbol);

    void ImportStage(string nodeId, string alias, string sourceNodeId, string sourceSymbol);

    void ImportView(string nodeId, string alias, string sourceNodeId, string sourceSymbol);

    void DefineShot(string nodeId, string alias, ViewPatch patch);

    ViewPatch ResolveShot(string nodeId, string alias);
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

    public StageId ResolveStage(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        if (string.Equals(local, StageId.Main.Value, StringComparison.Ordinal))
            return StageId.Main;
        return stageImports.TryGetValue((node, local), out var imported)
            ? imported
            : new StageId(Qualify(node, "stages", local));
    }

    public ViewId ResolveView(string nodeId, string symbol)
    {
        var (node, local) = Validate(nodeId, symbol);
        if (string.Equals(local, ViewId.Main.Value, StringComparison.Ordinal))
            return ViewId.Main;
        return viewImports.TryGetValue((node, local), out var imported)
            ? imported
            : new ViewId(Qualify(node, "views", local));
    }

    public void ImportStage(
        string nodeId,
        string alias,
        string sourceNodeId,
        string sourceSymbol)
    {
        var (node, localAlias) = Validate(nodeId, alias);
        var source = ResolveLocalStage(sourceNodeId, sourceSymbol);
        AddImport(stageImports, (node, localAlias), source, "Stage");
    }

    public void ImportView(
        string nodeId,
        string alias,
        string sourceNodeId,
        string sourceSymbol)
    {
        var (node, localAlias) = Validate(nodeId, alias);
        var source = ResolveLocalView(sourceNodeId, sourceSymbol);
        AddImport(viewImports, (node, localAlias), source, "View");
    }

    public void DefineShot(string nodeId, string alias, ViewPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var (node, localAlias) = Validate(nodeId, alias);
        AddImport(shotAliases, (node, localAlias), patch, "Shot");
    }

    public ViewPatch ResolveShot(string nodeId, string alias)
    {
        var (node, localAlias) = Validate(nodeId, alias);
        if (!shotAliases.TryGetValue((node, localAlias), out var patch))
            throw new DirectionReferenceException(
                $"Shot alias '{localAlias}' is not defined in Node '{node}'.");
        return patch;
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

    private static void AddImport<T>(
        IDictionary<(string Node, string Alias), T> imports,
        (string Node, string Alias) key,
        T value,
        string kind)
    {
        if (!imports.TryAdd(key, value))
        {
            throw new DirectionReferenceException(
                $"{kind} import alias '{key.Alias}' is already defined in Node '{key.Node}'.");
        }
    }
}

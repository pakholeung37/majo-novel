#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors;

/// <summary>Resolves definitions and appearance metadata without creating actor instances.
/// The supplied factories and excluded definitions describe one target domain.</summary>
public sealed class ActorDefinitionResolver
{
    private readonly IReadOnlyDictionary<string, ActorDefinition> definitions;
    private readonly IReadOnlyDictionary<string, ActorDefinition> unsupportedDefinitions;
    private readonly IReadOnlyDictionary<string, IActorFactory> factories;
    private readonly ActorCreationConventions conventions;
    private readonly IRuntimeLogger logger;

    public ActorDefinitionResolver(
        IEnumerable<ActorDefinition> definitions,
        IEnumerable<IActorFactory> factories,
        ActorCreationConventions? conventions = null,
        IEnumerable<ActorDefinition>? unsupportedDefinitions = null,
        IRuntimeLogger? logger = null)
        : this(IndexDefinitions(definitions), IndexFactories(factories),
            IndexDefinitions(unsupportedDefinitions ?? Array.Empty<ActorDefinition>()),
            conventions ?? new(), logger ?? NullRuntimeLogger.Instance)
    {
    }

    internal ActorDefinitionResolver(
        IReadOnlyDictionary<string, ActorDefinition> definitions,
        IReadOnlyDictionary<string, IActorFactory> factories,
        IReadOnlyDictionary<string, ActorDefinition> unsupportedDefinitions,
        ActorCreationConventions conventions,
        IRuntimeLogger logger)
    {
        this.definitions = definitions;
        this.factories = factories;
        this.unsupportedDefinitions = unsupportedDefinitions;
        this.conventions = conventions;
        this.logger = logger;
        foreach (var id in unsupportedDefinitions.Keys)
            if (definitions.ContainsKey(id)) throw new DuplicateActorIdException(id);
    }

    /// <summary>Requires an explicit definition and a complete appearance. The caller
    /// supplies the previous appearance only when definition and factory identities match.</summary>
    public ResolvedActorAppearance ResolveExplicitAppearance(
        string definitionId, string? appearance = null,
        string? previousAppearance = null, string? requiredFactoryId = null)
        => ResolveAppearance(definitionId, appearance, previousAppearance, requiredFactoryId, strict: true);

    public ResolvedActorAppearance ResolveBackdropAppearance(string definitionId, string? appearance = null,
        string? previousAppearance = null, string? requiredFactoryId = null)
        => ResolveAppearance(definitionId, appearance, previousAppearance, requiredFactoryId, strict: false);

    private ResolvedActorAppearance ResolveAppearance(string definitionId, string? appearance,
        string? previousAppearance, string? requiredFactoryId, bool strict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        var definition = Resolve(new ActorSpawnRequest("main", ActorFactoryIds.Background,
            "Default", requiredFactoryId: requiredFactoryId)
        { DefinitionId = definitionId, RequireExplicitDefinition = strict, UseBackdropResourceConvention = !strict });
        var factory = GetFactory(definition);
        factory.ValidateDefinition(definition);
        var initial = factory.GetInitialAppearance(definition);
        if (string.IsNullOrWhiteSpace(initial))
            throw new ActorDefinitionException(definitionId, "Factory must provide a default appearance.");
        var resolved = factory.ResolveAppearance(definition,
            appearance is null ? initial : previousAppearance ?? initial, appearance ?? initial);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new ActorDefinitionException(definitionId, "Factory returned an empty resolved appearance.");
        return new(definition, factory, resolved);
    }

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorSpawnRequest request)
    {
        var definition = Resolve(request);
        var factory = GetFactory(definition);
        factory.ValidateDefinition(definition);
        var appearance = request.UseDefinitionDefaultAppearance
            ? factory.GetInitialAppearance(definition) : request.InitialAppearance;
        return appearance is null ? Array.Empty<ResourcePreloadEntry>()
            : factory.GetPreloadResources(definition, appearance);
    }

    public bool CanResolve(ActorSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { _ = GetFactory(Resolve(request)); return true; }
        catch (ActorException) { return false; }
    }

    private IActorFactory GetFactory(ActorDefinition definition) =>
        factories.TryGetValue(definition.FactoryId, out var factory) ? factory
            : throw new ActorFactoryNotFoundException(definition.FactoryId, definition.Id);

    private static Dictionary<string, ActorDefinition> IndexDefinitions(IEnumerable<ActorDefinition> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, ActorDefinition>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!result.TryAdd(value.Id, value)) throw new DuplicateActorIdException(value.Id);
        return result;
    }

    private static Dictionary<string, IActorFactory> IndexFactories(IEnumerable<IActorFactory> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, IActorFactory>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.FactoryId);
            if (!result.TryAdd(value.FactoryId, value)) throw new DuplicateActorFactoryException(value.FactoryId);
        }
        return result;
    }

    public ActorDefinition Resolve(ActorSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definitionId = request.DefinitionId ?? request.ActorId;
        ThrowIfUnsupported(definitionId);
        if (definitions.TryGetValue(definitionId, out var definition))
        {
            ValidateRequiredFactory(request, definition.FactoryId);
            return string.Equals(request.ActorId, definition.Id, StringComparison.Ordinal)
                ? definition
                : new ActorDefinition(
                    request.ActorId,
                    definition.FactoryId,
                    definition.FactoryData,
                    definition.DefinitionId);
        }

        if (request.RequireExplicitDefinition)
            throw new ActorDefinitionException(definitionId, "An explicit actor definition is required.");

        if (request.UseBackdropResourceConvention)
        {
            ValidateRequiredFactory(request, ActorFactoryIds.Background);
            var id = new ResourceKey(definitionId);
            var root = conventions.BackgroundsPath;
            var key = string.IsNullOrWhiteSpace(root) || id.Path.StartsWith(new ResourceKey(root).Path + "/", StringComparison.OrdinalIgnoreCase)
                ? id : ResourceKey.Combine(root, id.Path);
            return new ActorDefinition(request.ActorId, ActorFactoryIds.Background,
                new TextureActorDefinitionData(definitionId, string.IsNullOrWhiteSpace(root) ? "backgrounds" : root,
                    "Default", [])
                { ResourceId = definitionId, DefaultResourceKey = key }, definitionId);
        }

        if (request.FallbackFactoryId == ActorFactoryIds.SceneObject)
        {
            if (string.IsNullOrWhiteSpace(request.DefinitionId))
                throw new ActorDefinitionException(request.ActorId, "Object creation requires asset.");
            ValidateRequiredFactory(request, ActorFactoryIds.SceneObject);
            var id = new ResourceKey(definitionId);
            var root = conventions.ObjectsPath;
            var key = string.IsNullOrWhiteSpace(root) || id.Path.StartsWith(new ResourceKey(root).Path + "/", StringComparison.OrdinalIgnoreCase)
                ? id : ResourceKey.Combine(root, id.Path);
            return new ActorDefinition(request.ActorId, ActorFactoryIds.SceneObject,
                new TextureActorDefinitionData(definitionId, string.IsNullOrWhiteSpace(root) ? "objects" : root,
                    "Default", [])
                { ResourceId = definitionId, DefaultResourceKey = key, Anchor = new(0.5f, 0f) }, definitionId);
        }

        if (TryResolveConventionDefinition(request, out var conventionalDefinition))
        {
            ValidateRequiredFactory(request, conventionalDefinition.FactoryId);
            logger.Debug(
                "actor",
                "actor.definition_inferred",
                $"Actor '{request.ActorId}' used conventional '{conventionalDefinition.FactoryId}' configuration.",
                properties: new Dictionary<string, object?>
                {
                    ["actorId"] = request.ActorId,
                    ["factoryId"] = conventionalDefinition.FactoryId,
                    ["initialAppearance"] = request.InitialAppearance,
                });
            return conventionalDefinition;
        }

        if (!IsConventionFactory(request.FallbackFactoryId))
        {
            throw new ActorNotFoundException(request.ActorId);
        }

        ValidateRequiredFactory(request, request.FallbackFactoryId);
        var resourcePathPrefix = request.FallbackFactoryId == ActorFactoryIds.Character
            ? conventions.CharactersPath
            : conventions.BackgroundsPath;
        var inferred = new ActorDefinition(
            request.ActorId,
            request.FallbackFactoryId,
            new TextureActorDefinitionData(
                definitionId,
                resourcePathPrefix,
                request.InitialAppearance,
                [],
                request.InitialPosition ?? CharacterPositionPreset.Center)
            { ResourceId = definitionId },
            definitionId);
        logger.Debug(
            "actor",
            "actor.definition_inferred",
            $"Actor '{request.ActorId}' used conventional '{request.FallbackFactoryId}' configuration.",
            properties: new Dictionary<string, object?>
            {
                ["actorId"] = request.ActorId,
                ["factoryId"] = request.FallbackFactoryId,
                ["initialAppearance"] = request.InitialAppearance,
                ["resourcePathPrefix"] = resourcePathPrefix,
            });
        return inferred;
    }

    private void ThrowIfUnsupported(string definitionId)
    {
        if (unsupportedDefinitions.TryGetValue(definitionId, out var unsupported))
            throw new ActorDefinitionException(definitionId,
                $"Factory '{unsupported.FactoryId}' does not support this actor manager environment.");
    }

    private bool TryResolveConventionDefinition(
        ActorSpawnRequest request,
        out ActorDefinition definition)
    {
        ActorDefinition? match = null;
        foreach (var factory in factories.Values)
        {
            if (request.RequiredFactoryId is not null &&
                !string.Equals(factory.FactoryId, request.RequiredFactoryId, StringComparison.Ordinal))
                continue;
            if (factory is not IConventionActorFactory convention ||
                !convention.TryResolveDefinition(request, out var candidate))
            {
                continue;
            }

            if (!string.Equals(
                    candidate.FactoryId,
                    factory.FactoryId,
                    StringComparison.Ordinal))
            {
                throw new ActorDefinitionException(
                    request.ActorId,
                    $"Convention factory '{factory.FactoryId}' inferred definition for " +
                    $"factory '{candidate.FactoryId}'.");
            }

            if (match is not null)
                throw new ActorDefinitionException(
                    request.DefinitionId ?? request.ActorId,
                    $"Convention actor definition is ambiguous between factories '{match.FactoryId}' and '{candidate.FactoryId}'. Specify a factory explicitly.");
            match = candidate;
        }

        definition = match!;
        return match is not null;
    }

    private static void ValidateRequiredFactory(
        ActorSpawnRequest request,
        string factoryId)
    {
        if (request.RequiredFactoryId is not null &&
            !string.Equals(
                factoryId,
                request.RequiredFactoryId,
                StringComparison.Ordinal))
        {
            throw new ActorFactoryConflictException(
                request.ActorId,
                factoryId,
                request.RequiredFactoryId);
        }
    }

    private static bool IsConventionFactory(string factoryId) =>
        factoryId is ActorFactoryIds.Character or ActorFactoryIds.Background;
}

public sealed record ResolvedActorAppearance(
    ActorDefinition Definition, IActorFactory Factory, string Appearance);

#nullable enable

using System;
using System.Linq;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Actors.Godot;

public static class ActorManagerBuilder
{
    public static ActorManager Create(
        ActorFactoryContext context,
        ILocaleManager? locales = null,
        ActorCreationConventions? conventions = null,
        IActorPresentationHost? presentationHost = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var registry = context.Catalog.Registry;
        var registrations = registry.ForDomain(context.Domain);
        var compiledDefinitions = context.Catalog.Definitions;
        var definitions = compiledDefinitions
            .Where(definition => registry.GetRequired(definition.FactoryId).Domains.HasFlag(context.Domain))
            .ToArray();
        var unsupportedDefinitions = compiledDefinitions.Except(definitions).ToArray();
        var factories = registrations.Select(entry =>
        {
            var factory = entry.BuildFactory(context) ?? throw new ActorException(
                $"Actor factory builder '{entry.FactoryId}' returned null in domain '{context.Domain}'.");
            if (!string.Equals(factory.FactoryId, entry.FactoryId, StringComparison.Ordinal))
                throw new ActorException(
                    $"Actor factory builder '{entry.FactoryId}' returned factory '{factory.FactoryId}'.");
            return factory;
        }).ToArray();
        return new ActorManager(definitions, factories, locales, context.Logger, conventions,
            unsupportedDefinitions, presentationHost);
    }
}

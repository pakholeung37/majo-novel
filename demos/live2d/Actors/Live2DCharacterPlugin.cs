#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Plugins;

namespace Majo.Demos.Live2D.Actors;

public sealed class Live2DCharacterPlugin : IMajoPlugin
{
    public string Id => Live2DActorIds.Factory;

    public void ConfigureServices(RuntimeServiceRegistry services) =>
        services.AddSingleton<Live2DActorRegistry>(_ => new Live2DActorRegistry());

    public void Configure(PluginRegistrationContext context)
    {
        context.ActorFactories.Register(
            Live2DActorIds.Factory,
            ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            factoryContext => new Live2DCharacterActorFactory(factoryContext),
            (configured, _) => Compile(configured),
            typeof(Live2DActorRegistry));
        context.Commands.Add(
            "live2d motion",
            services => new Live2DMotionCommand(services.Get<Live2DActorRegistry>()),
            "live2d",
            "1.0");
    }

    private static ActorDefinition Compile(
        Majo.Runtime.Configuration.ActorDefinitionConfiguration configured)
    {
        var source = configured.FactoryConfiguration as Live2DCharacterConfiguration ??
            throw new ActorDefinitionException(
                configured.Id,
                $"Factory '{Live2DActorIds.Factory}' requires Live2D configuration.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.DefaultAppearance);
        if (source.DisplayHeight <= 0 || !float.IsFinite(source.DisplayHeight))
        {
            throw new ActorDefinitionException(configured.Id, "Display height must be positive and finite.");
        }

        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expression) in source.Expressions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            expressions.Add(name, expression);
        }
        if (!expressions.ContainsKey(source.DefaultAppearance))
        {
            throw new ActorDefinitionException(
                configured.Id,
                $"Default Live2D expression '{source.DefaultAppearance}' is not mapped.");
        }

        var motions = new Dictionary<string, Live2DMotionDefinition>(StringComparer.Ordinal);
        foreach (var (name, encoded) in source.Motions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var separator = encoded.LastIndexOf(':');
            if (separator < 0 ||
                !int.TryParse(encoded[(separator + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                throw new ActorDefinitionException(
                    configured.Id,
                    $"Motion '{name}' must use the form 'group:index'.");
            }
            motions.Add(name, new Live2DMotionDefinition(encoded[..separator], index));
        }

        return new ActorDefinition(
            configured.Id,
            configured.FactoryId,
            new Live2DCharacterDefinitionData(
                source.ModelPath,
                source.DefaultAppearance,
                source.InitialPosition,
                source.DisplayHeight,
                source.VisualOffset,
                expressions,
                motions));
    }
}

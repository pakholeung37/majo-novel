#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class TestCatalogBuilder
{
    private readonly RuntimeCommandRegistry registry;
    private IServiceResolver? services;

    public TestCatalogBuilder(
        CommandConverterRegistry? converters = null,
        CommandParameterGroupRegistry? groups = null)
    {
        registry = new RuntimeCommandRegistry(converters, groups);
        DirectionCommandModule.RegisterConverters(registry.Converters);
    }

    public RuntimeCommandRegistry Registry => registry;

    public TestCatalogBuilder Add(
        string name,
        Func<IServiceResolver, IRuntimeCommand> factory,
        string providerId = "test",
        string? version = null)
    {
        registry.Add(name, factory, providerId, version);
        return this;
    }

    public TestCatalogBuilder Add(
        IRuntimeCommand command,
        string providerId = "test",
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        registry.Add(command.Name, _ => command, providerId, version);
        return this;
    }

    public TestCatalogBuilder RegisterModule(IRuntimeCommandModule module)
    {
        registry.RegisterModule(module);
        return this;
    }

    public TestCatalogBuilder WithServices(IServiceResolver services)
    {
        this.services = services;
        return this;
    }

    public TestCatalogBuilder ConfigureConverters(Action<CommandConverterRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(registry.Converters);
        return this;
    }

    public RuntimeCommandCatalog Build() =>
        registry.BuildCatalog(new TestServices(services));

    private sealed class TestServices : IServiceResolver
    {
        private readonly IServiceResolver? inner;
        private readonly Timing.IEasingRegistry easings;
        public TestServices(IServiceResolver? inner)
        {
            this.inner = inner;
            var defaults = new Timing.EasingRegistry();
            Majo.Runtime.Timing.BuiltInEasings.RegisterDefaults(defaults);
            easings = defaults;
        }
        public T Resolve<T>() where T : class => TryGet<T>(out var service) && service is not null
            ? service : throw new InvalidOperationException($"Missing test service '{typeof(T)}'.");
        public bool TryGet<T>(out T? service) where T : class
        {
            if (inner is not null && inner.TryGet(out service)) return true;
            service = typeof(T) == typeof(Timing.IEasingRegistry) ? easings as T : null;
            return service is not null;
        }
        public object Resolve(Type type) => type == typeof(Timing.IEasingRegistry)
            ? Resolve<Timing.IEasingRegistry>() : inner?.Resolve(type) ?? throw new InvalidOperationException($"Missing test service '{type}'.");
    }

    public static BoundCommand<TArguments> CreateBound<TArguments>(
        RuntimeCommand<TArguments> command,
        IServiceResolver? services = null,
        Action<CommandConverterRegistry>? configureConverters = null)
        where TArguments : class
    {
        ArgumentNullException.ThrowIfNull(command);
        var builder = new TestCatalogBuilder();
        if (services is not null)
        {
            builder.WithServices(services);
        }
        if (configureConverters is not null)
        {
            builder.ConfigureConverters(configureConverters);
        }
        builder.Add(command);
        var catalog = builder.Build();
        return catalog.Get<TArguments>(command.Name);
    }

    public static RuntimeCommandCatalog CreateCatalog(
        IRuntimeCommand command,
        IServiceResolver? services = null,
        Action<CommandConverterRegistry>? configureConverters = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var builder = new TestCatalogBuilder();
        if (services is not null)
        {
            builder.WithServices(services);
        }
        if (configureConverters is not null)
        {
            builder.ConfigureConverters(configureConverters);
        }
        builder.Add(command);
        return builder.Build();
    }
}

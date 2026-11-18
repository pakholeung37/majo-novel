#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public interface ICommandConverterRegistrar
{
    ICommandConverterRegistrar AddDefault<T>(
        Func<IServiceResolver, ICommandValueConverter<T>> factory,
        string providerId);

    ICommandConverterRegistrar AddDefault<T>(
        ICommandValueConverter<T> converter,
        string providerId = "test") =>
        AddDefault<T>(_ => converter, providerId);

    ICommandConverterRegistrar AddDefault<T>(
        Func<string, T> parse,
        string providerId = "test") =>
        AddDefault<T>(_ => new DelegateCommandValueConverter<T>(parse), providerId);

    ICommandConverterRegistrar ReplaceDefault<T>(
        Func<IServiceResolver, ICommandValueConverter<T>> factory,
        string providerId,
        string expectedProviderId);

    ICommandConverterRegistrar AddFactory<TConverter>(
        Func<IServiceResolver, TConverter> factory,
        string providerId)
        where TConverter : class, ICommandValueConverter;
}

public sealed class CommandConverterRegistry : ICommandConverterRegistrar
{
    private readonly Dictionary<Type, DefaultConverterRegistration> defaultConverters = new();
    private readonly Dictionary<Type, FactoryConverterRegistration> factoryConverters = new();
    private bool isSealed;

    public CommandConverterRegistry()
    {
        RegisterBuiltInDefaults();
    }

    private void RegisterBuiltInDefaults()
    {
        AddDefault<string>(_ => StringCommandValueConverter.Instance, "core");
        AddDefault<bool>(_ => BooleanCommandValueConverter.Instance, "core");
        AddDefault<int>(_ => Int32CommandValueConverter.Instance, "core");
        AddDefault<double>(_ => DoubleCommandValueConverter.Instance, "core");
        AddDefault<float>(_ => SingleCommandValueConverter.Instance, "core");
    }

    public void Seal()
    {
        isSealed = true;
    }

    public bool IsSealed => isSealed;

    public ICommandConverterRegistrar AddDefault<T>(
        Func<IServiceResolver, ICommandValueConverter<T>> factory,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        EnsureMutable();

        var targetType = typeof(T);
        if (defaultConverters.TryGetValue(targetType, out var existing))
        {
            throw new RuntimeCommandException(
                "registry",
                $"Default command converter for type '{targetType.FullName}' is already registered by provider '{existing.ProviderId}'.",
                phase: CommandFailurePhase.Schema);
        }

        defaultConverters[targetType] = new DefaultConverterRegistration(
            targetType,
            services => factory(services),
            providerId);

        return this;
    }

    public CommandConverterRegistry AddDefault<T>(
        ICommandValueConverter<T> converter,
        string providerId = "test")
    {
        ArgumentNullException.ThrowIfNull(converter);
        AddDefault<T>(_ => converter, providerId);
        return this;
    }

    public CommandConverterRegistry AddDefault<T>(
        Func<string, T> parse,
        string providerId = "test")
    {
        ArgumentNullException.ThrowIfNull(parse);
        AddDefault<T>(_ => new DelegateCommandValueConverter<T>(parse), providerId);
        return this;
    }

    public ICommandConverterRegistrar ReplaceDefault<T>(
        Func<IServiceResolver, ICommandValueConverter<T>> factory,
        string providerId,
        string expectedProviderId)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProviderId);
        EnsureMutable();

        var targetType = typeof(T);
        if (!defaultConverters.TryGetValue(targetType, out var existing))
        {
            throw new RuntimeCommandException(
                "registry",
                $"Cannot replace default command converter for type '{targetType.FullName}': no existing registration found.",
                phase: CommandFailurePhase.Schema);
        }

        if (!string.Equals(existing.ProviderId, expectedProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeCommandException(
                "registry",
                $"Cannot replace default command converter for type '{targetType.FullName}': expected provider '{expectedProviderId}' but was '{existing.ProviderId}'.",
                phase: CommandFailurePhase.Schema);
        }

        defaultConverters[targetType] = new DefaultConverterRegistration(
            targetType,
            services => factory(services),
            providerId);

        return this;
    }

    public ICommandConverterRegistrar AddFactory<TConverter>(
        Func<IServiceResolver, TConverter> factory,
        string providerId)
        where TConverter : class, ICommandValueConverter
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        EnsureMutable();

        var converterType = typeof(TConverter);
        if (factoryConverters.TryGetValue(converterType, out var existing))
        {
            throw new RuntimeCommandException(
                "registry",
                $"Converter factory for '{converterType.FullName}' is already registered by provider '{existing.ProviderId}'.",
                phase: CommandFailurePhase.Schema);
        }

        factoryConverters[converterType] = new FactoryConverterRegistration(
            converterType,
            services => factory(services),
            providerId);

        return this;
    }

    public bool HasDefault(Type type)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        return defaultConverters.ContainsKey(targetType);
    }

    public ResolvedCommandConverters Resolve(IServiceResolver services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolvedDefaults = new Dictionary<Type, ICommandValueConverter>();
        var resolvedFactories = new Dictionary<Type, ICommandValueConverter>();

        foreach (var (type, reg) in defaultConverters)
        {
            try
            {
                var instance = reg.Factory(services);
                if (instance is null)
                {
                    throw new InvalidOperationException(
                        $"Factory for default converter '{type.FullName}' returned null.");
                }

                resolvedDefaults[type] = instance;
            }
            catch (Exception ex)
            {
                throw new RuntimeCommandException(
                    "catalog",
                    $"Failed to resolve default converter for '{type.FullName}' from provider '{reg.ProviderId}': {ex.Message}",
                    ex,
                    phase: CommandFailurePhase.Schema);
            }
        }

        foreach (var (type, reg) in factoryConverters)
        {
            try
            {
                var instance = reg.Factory(services);
                if (instance is null)
                {
                    throw new InvalidOperationException(
                        $"Factory for converter type '{type.FullName}' returned null.");
                }

                resolvedFactories[type] = instance;
            }
            catch (Exception ex)
            {
                throw new RuntimeCommandException(
                    "catalog",
                    $"Failed to resolve converter factory for '{type.FullName}' from provider '{reg.ProviderId}': {ex.Message}",
                    ex,
                    phase: CommandFailurePhase.Schema);
            }
        }

        return new ResolvedCommandConverters(resolvedDefaults, resolvedFactories);
    }

    private void EnsureMutable()
    {
        if (isSealed)
        {
            throw new InvalidOperationException("Converters cannot be modified after registration is sealed.");
        }
    }

    internal CommandConverterSnapshot CreateSnapshot()
    {
        return new CommandConverterSnapshot(
            new Dictionary<Type, DefaultConverterRegistration>(defaultConverters),
            new Dictionary<Type, FactoryConverterRegistration>(factoryConverters));
    }

    internal void RestoreSnapshot(CommandConverterSnapshot snapshot)
    {
        defaultConverters.Clear();
        foreach (var (k, v) in snapshot.Defaults)
        {
            defaultConverters[k] = v;
        }

        factoryConverters.Clear();
        foreach (var (k, v) in snapshot.Factories)
        {
            factoryConverters[k] = v;
        }
    }

    internal sealed record DefaultConverterRegistration(
        Type TargetType,
        Func<IServiceResolver, ICommandValueConverter> Factory,
        string ProviderId);

    internal sealed record FactoryConverterRegistration(
        Type ConverterType,
        Func<IServiceResolver, ICommandValueConverter> Factory,
        string ProviderId);

    internal sealed record CommandConverterSnapshot(
        Dictionary<Type, DefaultConverterRegistration> Defaults,
        Dictionary<Type, FactoryConverterRegistration> Factories);
}

public sealed class ResolvedCommandConverters
{
    private readonly IReadOnlyDictionary<Type, ICommandValueConverter> defaultConverters;
    private readonly IReadOnlyDictionary<Type, ICommandValueConverter> factoryConverters;
    private readonly Dictionary<Type, ICommandValueConverter> instantiatedParameterless = new();

    public ResolvedCommandConverters(
        IReadOnlyDictionary<Type, ICommandValueConverter> defaultConverters,
        IReadOnlyDictionary<Type, ICommandValueConverter> factoryConverters)
    {
        this.defaultConverters = defaultConverters;
        this.factoryConverters = factoryConverters;
    }

    public ICommandValueConverter? FindDefault(Type type)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        return defaultConverters.TryGetValue(targetType, out var converter) ? converter : null;
    }

    public ICommandValueConverter<T>? FindDefault<T>() =>
        FindDefault(typeof(T)) as ICommandValueConverter<T>;

    public ICommandValueConverter GetExplicitOrFactory(Type converterType, Func<ICommandValueConverter>? parameterlessFactory = null)
    {
        if (factoryConverters.TryGetValue(converterType, out var instance))
        {
            return instance;
        }

        lock (instantiatedParameterless)
        {
            if (instantiatedParameterless.TryGetValue(converterType, out var cached))
            {
                return cached;
            }

            if (parameterlessFactory is null)
                throw new RuntimeCommandException("catalog", $"Parameter converter '{converterType.FullName}' requires an explicit factory.", phase: CommandFailurePhase.Schema);
            var created = parameterlessFactory();
            if (created is null || created.GetType() != converterType)
                throw new RuntimeCommandException("catalog", $"Factory for '{converterType.FullName}' returned an incompatible converter.", phase: CommandFailurePhase.Schema);
            instantiatedParameterless[converterType] = created;
            return created;
        }
    }

    public ICommandValueConverter ResolveForParameter(
        Type valueType,
        Type? explicitConverterType,
        string commandName,
        string parameterName,
        Func<ICommandValueConverter>? parameterlessFactory = null)
    {
        var targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;

        if (explicitConverterType is not null)
        {
            var converter = GetExplicitOrFactory(explicitConverterType, parameterlessFactory);
            if (converter.TargetType != targetType)
            {
                throw new RuntimeCommandException(
                    commandName,
                    $"Explicit converter '{explicitConverterType.FullName}' targets type '{converter.TargetType.FullName}', which is not compatible with parameter '{parameterName}' of type '{targetType.FullName}'.",
                    phase: CommandFailurePhase.Schema);
            }

            return converter;
        }

        var defaultConverter = FindDefault(targetType);
        if (defaultConverter is not null)
        {
            return defaultConverter;
        }

        throw new RuntimeCommandException(
            commandName,
            $"No command value converter is registered for type '{targetType.FullName}' on parameter '{parameterName}'.",
            phase: CommandFailurePhase.Schema);
    }
}

public sealed class DelegateCommandValueConverter<T> : ICommandValueConverter<T>
{
    private readonly Func<string, T> parse;

    public DelegateCommandValueConverter(Func<string, T> parse)
    {
        this.parse = parse ?? throw new ArgumentNullException(nameof(parse));
    }

    public Type TargetType => typeof(T);

    public T Parse(string text) => parse(text);

    public object? ParseUntyped(string text) => Parse(text);
}


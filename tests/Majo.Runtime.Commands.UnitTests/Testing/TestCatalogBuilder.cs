using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Commands.UnitTests.Testing;

internal sealed class TestCatalogBuilder
{
    private readonly RuntimeCommandRegistry registry = new();

    public TestCatalogBuilder Add(string name, Func<IServiceResolver, IRuntimeCommand> factory)
    {
        registry.Add(name, factory, "test");
        return this;
    }

    public TestCatalogBuilder ConfigureConverters(Action<CommandConverterRegistry> configure)
    {
        configure(registry.Converters);
        return this;
    }

    public RuntimeCommandCatalog Build() => registry.BuildCatalog();

    public static BoundCommand<TArguments> CreateBound<TArguments>(RuntimeCommand<TArguments> command,
        Action<CommandConverterRegistry>? configureConverters = null) where TArguments : class =>
        CreateCatalog(command, configureConverters).Get<TArguments>(command.Name);

    public static RuntimeCommandCatalog CreateCatalog(IRuntimeCommand command,
        Action<CommandConverterRegistry>? configureConverters = null)
    {
        var builder = new TestCatalogBuilder().Add(command.Name, _ => command);
        if (configureConverters is not null) builder.ConfigureConverters(configureConverters);
        return builder.Build();
    }
}

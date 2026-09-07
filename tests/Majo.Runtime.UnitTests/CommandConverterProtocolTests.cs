#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CommandConverterProtocolTests
{
    private readonly record struct CustomCoordinate(int X, int Y);

    private sealed class CustomCoordinateConverter : ICommandValueConverter<CustomCoordinate>, ICommandValueDescriptor<CustomCoordinate>
    {
        public CommandValueDescription? Description => new(DisplayName: "CustomCoordinate", Syntax: "<x:y>");

        public CustomCoordinate Parse(string text)
        {
            var parts = text.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
            {
                throw new CommandValueFormatException("must be x:y");
            }

            return new CustomCoordinate(x, y);
        }

        public string Format(CustomCoordinate value) => $"{value.X}:{value.Y}";
    }

    private sealed class OverriddenCoordinateConverter : ICommandValueConverter<CustomCoordinate>
    {
        public CustomCoordinate Parse(string text) => new(999, 999);
    }

    private enum TestStatus
    {
        Pending,
        Active,
        Completed,
    }

    [Fact]
    public void StandardConverters_ParseValidValues_AndRejectInvalid()
    {
        Assert.Equal("hello", StringCommandValueConverter.Instance.Parse("hello"));

        Assert.True(BooleanCommandValueConverter.Instance.Parse("true"));
        Assert.False(BooleanCommandValueConverter.Instance.Parse("false"));
        Assert.Throws<CommandValueFormatException>(() => BooleanCommandValueConverter.Instance.Parse("not-a-bool"));

        Assert.Equal(42, Int32CommandValueConverter.Instance.Parse("42"));
        Assert.Equal(-10, Int32CommandValueConverter.Instance.Parse("-10"));
        Assert.Throws<CommandValueFormatException>(() => Int32CommandValueConverter.Instance.Parse("3.14"));

        Assert.Equal(3.14, DoubleCommandValueConverter.Instance.Parse("3.14"), 2);
        Assert.Throws<CommandValueFormatException>(() => DoubleCommandValueConverter.Instance.Parse("NaN"));
        Assert.Throws<CommandValueFormatException>(() => DoubleCommandValueConverter.Instance.Parse("Infinity"));

        Assert.Equal(1.5f, SingleCommandValueConverter.Instance.Parse("1.5"), 1);
        Assert.Throws<CommandValueFormatException>(() => SingleCommandValueConverter.Instance.Parse("NaN"));
    }

    [Fact]
    public void EnumCommandValueConverter_RejectsNumericInputs_AndParsesAliases()
    {
        var aliases = new Dictionary<string, TestStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["done"] = TestStatus.Completed,
            ["running"] = TestStatus.Active,
        };
        var converter = new EnumCommandValueConverter<TestStatus>(aliases);

        Assert.Equal(TestStatus.Active, converter.Parse("active"));
        Assert.Equal(TestStatus.Active, converter.Parse("ACTIVE"));
        Assert.Equal(TestStatus.Active, converter.Parse("running"));
        Assert.Equal(TestStatus.Completed, converter.Parse("done"));

        // Reject numbers
        var numEx = Assert.Throws<CommandValueFormatException>(() => converter.Parse("1"));
        Assert.Contains("Numeric values are not allowed", numEx.Message);

        // Reject invalid name
        var invalidEx = Assert.Throws<CommandValueFormatException>(() => converter.Parse("unknown"));
        Assert.Contains("must be one of", invalidEx.Message);
    }

    [Fact]
    public void DomainConverters_CameraShakeAxis_Vector2_MotionCurve_ParseAccurately()
    {
        // CameraShakeAxis
        Assert.Equal(CameraShakeAxis.All, CameraShakeAxisConverter.Instance.Parse("all"));
        Assert.Equal(CameraShakeAxis.All, CameraShakeAxisConverter.Instance.Parse("both"));
        Assert.Equal(CameraShakeAxis.X, CameraShakeAxisConverter.Instance.Parse("x"));
        Assert.Equal(CameraShakeAxis.Y, CameraShakeAxisConverter.Instance.Parse("y"));
        Assert.Equal(CameraShakeAxis.Diagonal, CameraShakeAxisConverter.Instance.Parse("diagonal"));
        Assert.Equal(CameraShakeAxis.Diagonal, CameraShakeAxisConverter.Instance.Parse("diag"));
        Assert.Throws<CommandValueFormatException>(() => CameraShakeAxisConverter.Instance.Parse("invalid"));

        // Vector2
        var v = Vector2Converter.Instance.Parse("12.5, -4.2");
        Assert.Equal(12.5f, v.X);
        Assert.Equal(-4.2f, v.Y);
        Assert.Throws<CommandValueFormatException>(() => Vector2Converter.Instance.Parse("12"));
        Assert.Throws<CommandValueFormatException>(() => Vector2Converter.Instance.Parse("12,NaN"));

        // MotionCurve
        Assert.Equal(MotionCurve.Linear, MotionCurveConverter.Instance.Parse("linear"));
        Assert.Equal(MotionCurve.CubicOut, MotionCurveConverter.Instance.Parse("cubic-out"));
        Assert.Equal(MotionCurve.BackOut, MotionCurveConverter.Instance.Parse("back-out"));
        Assert.Throws<CommandValueFormatException>(() => MotionCurveConverter.Instance.Parse("nonexistent"));
    }

    [Fact]
    public void ConverterRegistry_AddDefault_And_ResolveWithDependencies()
    {
        var easings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(easings);
        var services = new ServiceResolverBuilder()
            .Add<IEasingRegistry>(easings)
            .Build();

        var registry = new CommandConverterRegistry();
        registry.AddDefault<CustomCoordinate>(_ => new CustomCoordinateConverter(), "tests");
        registry.AddDefault<EasingId>(s => new EasingIdConverter(s.Resolve<IEasingRegistry>()), "timing");

        var resolved = registry.Resolve(services);
        var coordConv = resolved.FindDefault<CustomCoordinate>();
        Assert.NotNull(coordConv);
        Assert.Equal(new CustomCoordinate(10, 20), coordConv!.Parse("10:20"));

        var easingConv = resolved.FindDefault<EasingId>();
        Assert.NotNull(easingConv);
        Assert.Equal(EasingId.SineIn, easingConv!.Parse("sine-in"));
    }

    [Fact]
    public void ConverterRegistry_RejectsDuplicateDefault_UnlessReplacedWithExpectedProvider()
    {
        var registry = new CommandConverterRegistry();
        registry.AddDefault<CustomCoordinate>(_ => new CustomCoordinateConverter(), "providerA");

        var duplicate = Assert.Throws<RuntimeCommandException>(() =>
            registry.AddDefault<CustomCoordinate>(_ => new CustomCoordinateConverter(), "providerB"));
        Assert.Contains("already registered by provider 'providerA'", duplicate.Message);

        var wrongExpected = Assert.Throws<RuntimeCommandException>(() =>
            registry.ReplaceDefault<CustomCoordinate>(_ => new OverriddenCoordinateConverter(), "providerB", "wrongProvider"));
        Assert.Contains("expected provider 'wrongProvider' but was 'providerA'", wrongExpected.Message);

        // Successful replacement
        registry.ReplaceDefault<CustomCoordinate>(_ => new OverriddenCoordinateConverter(), "providerB", "providerA");
        var resolved = registry.Resolve(EmptyServiceResolver.Instance);
        Assert.Equal(new CustomCoordinate(999, 999), resolved.FindDefault<CustomCoordinate>()!.Parse("anything"));
    }

    [Fact]
    public void ConverterRegistry_SealedRejectsModifications()
    {
        var registry = new CommandConverterRegistry();
        registry.Seal();

        Assert.Throws<InvalidOperationException>(() =>
            registry.AddDefault<CustomCoordinate>(_ => new CustomCoordinateConverter(), "test"));

        Assert.Throws<InvalidOperationException>(() =>
            registry.ReplaceDefault<string>(_ => StringCommandValueConverter.Instance, "test", "core"));
    }

    [Fact]
    public void ModuleTransactionRollsBackAllRegistriesOnException()
    {
        var converterRegistry = new CommandConverterRegistry();
        var groupRegistry = new CommandParameterGroupRegistry();
        var commandRegistry = new RuntimeCommandRegistry(converterRegistry, groupRegistry);

        // Staged module that succeeds partially and then throws
        var failingModule = new TransactionalFailingModule();

        Assert.Throws<InvalidOperationException>(() =>
            commandRegistry.RegisterModule(failingModule));

        // Verify none of the registrations remained
        Assert.DoesNotContain(commandRegistry.Registrations, r => r.Name == "test.cmd");
        Assert.False(converterRegistry.HasDefault(typeof(CustomCoordinate)));
        Assert.False(groupRegistry.TryGet("test.group", out _));
    }

    private sealed class TransactionalFailingModule : IRuntimeCommandModule
    {
        public void Register(CommandRegistrationContext context)
        {
            context.Commands.Add("test.cmd", _ => throw new NotImplementedException(), "test");
            context.Converters.AddDefault<CustomCoordinate>(_ => new CustomCoordinateConverter(), "test");
            context.Groups.Add("test.group", new CommandGroupDeclaration(
                "test.group",
                "test.group",
                0,
                typeof(object),
                Parameters: Array.Empty<CommandParameterDeclaration>()));
            throw new InvalidOperationException("Simulation of failure midway through module registration");
        }
    }

    private sealed class EmptyServiceResolver : IServiceResolver
    {
        public static EmptyServiceResolver Instance { get; } = new();

        public TService Resolve<TService>() where TService : class =>
            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' is not registered.");

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            service = null;
            return false;
        }

        public object Resolve(Type serviceType) =>
            throw new InvalidOperationException($"Service '{serviceType.FullName}' is not registered.");
    }
}

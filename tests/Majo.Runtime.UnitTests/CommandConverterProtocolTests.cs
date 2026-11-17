#nullable enable

using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
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

    [Fact]
    public void DomainConverters_CameraShakeAxis_Vector2_ParseAccurately()
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
        registry.AddDefault(s => new EasingIdConverter(s.Resolve<IEasingRegistry>()), "timing");

        var resolved = registry.Resolve(services);
        var coordConv = resolved.FindDefault<CustomCoordinate>();
        Assert.NotNull(coordConv);
        Assert.Equal(new CustomCoordinate(10, 20), coordConv!.Parse("10:20"));

        var easingConv = resolved.FindDefault<EasingId>();
        Assert.NotNull(easingConv);
        Assert.Equal(EasingId.SineIn, easingConv!.Parse("sine-in"));
    }
}

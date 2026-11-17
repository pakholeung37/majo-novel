#nullable enable

using Majo.Runtime.Direction.Core;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class SplitLayoutTests
{

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(101, 0)]
    [InlineData(50, 90)]
    [InlineData(50, -90)]
    [InlineData(float.NaN, 0)]
    [InlineData(50, float.PositiveInfinity)]
    public void InvalidGeometryIsRejected(float divider, float angle) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitLayout(divider, angle).Validate());
}

using System.Buffers.Binary;
using Majo.Runtime.Preloading;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class PreloadSizeEstimatorTests
{
    [Fact]
    public void LargeAtlasUsesExpandedDimensionsInsteadOfSmallDefaultOrDiskSize()
    {
        var header = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        "IHDR"u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 8192);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 8192);
        Assert.Equal(8192L * 8192 * 4 * 4 / 3, PreloadSizeEstimator.EstimatePng(header, 32 * 1024 * 1024));
        Assert.Equal(1024L * 1024 * 1024, PreloadSizeEstimator.EstimatePng(header, 1024L * 1024 * 1024));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), uint.MaxValue);
        Assert.Equal(1234, PreloadSizeEstimator.EstimatePng(header, 1234));
        Assert.Equal(1234, PreloadSizeEstimator.EstimatePng(Array.Empty<byte>(), 1234));
    }
}

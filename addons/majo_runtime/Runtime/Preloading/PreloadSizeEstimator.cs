#nullable enable

using System;
using System.Buffers.Binary;

namespace Majo.Runtime.Preloading;

public static class PreloadSizeEstimator
{
    // RGBA8 with a full mip chain; estimates residency, not compressed file size.
    public static long EstimatePng(ReadOnlySpan<byte> header, long fallback)
    {
        ReadOnlySpan<byte> signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (header.Length < 24 || !header[..8].SequenceEqual(signature) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8)) return fallback;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width == 0 || height == 0 || width > 65536 || height > 65536) return fallback;
        return Math.Max(fallback, (long)width * height * 4 * 4 / 3);
    }
}

#nullable enable

using System;
using System.Buffers.Binary;

namespace Majo.Runtime.Preloading;

public static class PreloadSizeEstimator
{
    // Conservative RGBA8 estimate; honor the imported mipmap setting when available.
    public static long EstimatePng(ReadOnlySpan<byte> header, long fallback, bool mipmaps = true)
    {
        ReadOnlySpan<byte> signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (header.Length < 24 || !header[..8].SequenceEqual(signature) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8)) return fallback;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width == 0 || height == 0 || width > 65536 || height > 65536) return fallback;
        var bytes = (long)width * height * 4;
        return Math.Max(fallback, mipmaps ? bytes * 4 / 3 : bytes);
    }
}

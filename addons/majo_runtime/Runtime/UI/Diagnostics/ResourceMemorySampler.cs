#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.UI.Diagnostics;

/// <summary>Payload estimates, independent of cache budgets. Does not own sampled resources.</summary>
internal sealed class ResourceMemorySampler
{
    private readonly ConditionalWeakTable<object, AudioSize> audioSizes = new();

    public string Sample(IResourceManager resources)
        => Sample(resources.CaptureDiagnosticResources());

    internal string Sample(IReadOnlyList<ResourceDiagnosticEntry> entries)
    {
        var unique = entries.GroupBy(entry => entry.Resource is GodotObject godot
            ? (object)godot.GetInstanceId() : entry.Resource).ToArray();
        var textures = new HashSet<Rid>();
        var audio = new HashSet<ulong>();
        long textureBytes = 0, audioBytes = 0;
        int pinned = 0, leased = 0, cached = 0, unknown = 0;
        foreach (var group in unique)
        {
            if (group.Any(entry => entry.PinnedLeaseCount > 0)) pinned++;
            else if (group.Any(entry => entry.LeaseCount > 0)) leased++;
            else cached++;
            var resource = group.First().Resource;
            if (resource is Texture2D texture)
            {
                // Atlas regions share the backing texture's allocation.
                while (texture is AtlasTexture atlas && atlas.Atlas is not null) texture = atlas.Atlas;
                if (!textures.Add(texture.GetRid())) continue;
                var size = EstimateTexture(texture);
                if (size is null) unknown++;
                else textureBytes += size.Value;
            }
            else if (resource is AudioStream stream)
            {
                object payload = stream is AudioStreamOggVorbis ogg && ogg.PacketSequence is not null
                    ? ogg.PacketSequence : stream;
                if (payload is GodotObject native && !audio.Add(native.GetInstanceId())) continue;
                var size = audioSizes.GetValue(payload, ReadAudioSize).Bytes;
                if (size is null) unknown++;
                else audioBytes += size.Value;
            }
            else unknown++;
        }
        return FormattableString.Invariant($"RM Tex ≈{textureBytes / 1048576.0,4:0}M · Aud ≈{audioBytes / 1048576.0,3:0}M | Res {unique.Length,3} (P{pinned,2}/L{leased,2}/C{cached,2})")
            + (unknown > 0 ? $" · ?{unknown}" : string.Empty);
    }

    private static AudioSize ReadAudioSize(object resource)
    {
        // Godot copies packed arrays into managed memory. Read once, retain only the length.
        long? bytes = resource switch
        {
            AudioStreamWav wav => wav.Data.LongLength,
            AudioStreamMP3 mp3 => mp3.Data.LongLength,
            OggPacketSequence packets => packets.PacketData.Sum(page => page.Sum(packet => packet.AsByteArray().LongLength)),
            _ => null,
        };
        return new AudioSize(bytes);
    }

    private static long? EstimateTexture(Texture2D texture)
    {
        var format = texture.GetFormat().ToString();
        var bytesPerPixel = format switch
        {
            "L8" or "R8" => 1, "La8" or "Rg8" or "Rh" => 2,
            "Rgb8" => 3, "Rgba8" or "Rgbe9995" => 4, "Rgba4444" or "Rgb565" => 2,
            "Rf" or "Rgh" => 4, "Rgbh" => 6, "Rgf" or "Rgbah" => 8,
            "Rgbf" => 12, "Rgbaf" => 16, _ => 0,
        };
        var blockBytes = format switch
        {
            "Dxt1" or "RgtcR" or "Etc" or "Etc2R11" or "Etc2R11S" or "Etc2Rgb8" or "Etc2Rgb8A1" => 8,
            "Dxt5RaAsRg" or "Etc2RaAsRg" or "Dxt3" or "Dxt5" or "RgtcRg" or "BptcRgba" or "BptcRgbf" or "BptcRgbfu" or "Etc2Rg11" or "Etc2Rg11S" or "Etc2Rgba8" => 16,
            _ => 0,
        };
        if (bytesPerPixel == 0 && blockBytes == 0) return null;
        long total = 0;
        int width = texture.GetWidth(), height = texture.GetHeight();
        if (width <= 0 || height <= 0) return null;
        var mipmaps = texture.GetMipmapCount();
        var level = 0;
        do
        {
            total += blockBytes > 0 ? ((width + 3L) / 4) * ((height + 3L) / 4) * blockBytes
                : (long)width * height * bytesPerPixel;
            if (level++ >= mipmaps || (width == 1 && height == 1)) break;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        } while (true);
        return total;
    }

    private sealed record AudioSize(long? Bytes);
}

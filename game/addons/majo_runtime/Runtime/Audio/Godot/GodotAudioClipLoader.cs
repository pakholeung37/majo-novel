#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio.Godot;

public sealed class GodotAudioClip : IAudioClip
{
    public GodotAudioClip(string id, AudioStream stream)
    {
        Id = id;
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        var length = stream.GetLength();
        Duration = double.IsFinite(length) && length > 0
            ? TimeSpan.FromSeconds(length)
            : null;
    }

    public string Id { get; }

    public TimeSpan? Duration { get; }

    public AudioStream Stream { get; }
}

public sealed class GodotAudioClipLoader : IAudioClipLoader
{
    private readonly IResourceLoader<AudioStream> loader;

    public GodotAudioClipLoader(IResourceManager resources, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(resources);
        loader = resources.CreateLoader<AudioStream>(pathPrefix);
    }

    public string PathPrefix => loader.PathPrefix;

    public bool Exists(string clipId, string? overridePath = null) =>
        loader.Exists(clipId, overridePath);

    public async Task<IAudioClip> LoadAsync(
        string clipId,
        string? overridePath = null,
        CancellationToken cancellationToken = default) => new GodotAudioClip(
            clipId,
            await loader.LoadAsync(clipId, overridePath, cancellationToken));
}

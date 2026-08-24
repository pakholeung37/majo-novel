#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Save.Godot;

public sealed class GodotSaveThumbnailProvider : ISaveThumbnailProvider
{
    private readonly Node owner;
    private readonly string rootPath;
    private readonly int width;
    private readonly int height;
    private bool isInitialized;

    public GodotSaveThumbnailProvider(
        Node owner,
        string storageNamespace,
        int width,
        int height)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageNamespace);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        rootPath = Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("user://"),
            storageNamespace.Replace('/', Path.DirectorySeparatorChar)));
        this.width = width;
        this.height = height;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public Task<SaveThumbnailResult> CaptureAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var viewport = owner.GetViewport();
            if (viewport is null || !GodotObject.IsInstanceValid(viewport))
            {
                return Task.FromResult(SaveThumbnailResult.Missing(
                    "No active viewport is available."));
            }
            var image = viewport.GetTexture()?.GetImage();
            if (image is null || image.IsEmpty())
            {
                return Task.FromResult(SaveThumbnailResult.Missing(
                    "The active viewport did not provide an image."));
            }
            image.Resize(width, height, Image.Interpolation.Lanczos);
            var logicalReference =
                $"thumbnails/{slot.Kind.ToString().ToLowerInvariant()}/" +
                $"{slot.Index:D4}-{Guid.NewGuid():N}.png";
            var path = Resolve(logicalReference);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
            try
            {
                var error = image.SavePng(temporaryPath);
                if (error != Error.Ok)
                {
                    return Task.FromResult(SaveThumbnailResult.Missing(
                        $"PNG encoding failed with Godot error '{error}'."));
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: false);
                return Task.FromResult(new SaveThumbnailResult(logicalReference, null));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Task.FromResult(SaveThumbnailResult.Missing(exception.Message));
        }
    }

    public Task DeleteAsync(
        string logicalReference,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(logicalReference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string Resolve(string logicalReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalReference);
        if (!logicalReference.StartsWith("thumbnails/", StringComparison.Ordinal) ||
            !logicalReference.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            logicalReference.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(logicalReference))
        {
            throw new SaveSlotException(
                $"Thumbnail reference '{logicalReference}' is invalid.");
        }
        var path = Path.GetFullPath(Path.Combine(
            rootPath,
            logicalReference.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SaveSlotException("Thumbnail reference escapes the storage namespace.");
        }
        return path;
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new SaveSlotException("Thumbnail provider is not initialized.");
        }
    }
}

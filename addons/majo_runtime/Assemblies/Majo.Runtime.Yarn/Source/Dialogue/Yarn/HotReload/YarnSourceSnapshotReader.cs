#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed class YarnSourceSnapshotReader
{
    private const int MaximumAttempts = 3;

    public static async Task<YarnSourceSnapshot> ReadAsync(
        string projectId,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadOnceAsync(
                    projectId,
                    fullManifestPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (YarnSourceChangedDuringReadException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(30 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(30 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new YarnSourceChangedDuringReadException(
            $"Yarn sources for project '{projectId}' did not settle after {MaximumAttempts} attempts.");
    }

    private static async Task<YarnSourceSnapshot> ReadOnceAsync(
        string projectId,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifestBefore = CaptureMetadata(manifestPath);
        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestText) ??
            throw new InvalidDataException($"Yarn manifest '{manifestPath}' is empty.");
        var root = Path.GetDirectoryName(manifestPath) ??
            throw new InvalidDataException($"Yarn manifest '{manifestPath}' has no parent directory.");

        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var pattern in manifest.SourceFiles ?? Array.Empty<string>())
        {
            matcher.AddInclude(NormalizePattern(pattern));
        }
        foreach (var pattern in manifest.ExcludeFiles ?? Array.Empty<string>())
        {
            matcher.AddExclude(NormalizePattern(pattern));
        }

        var paths = matcher.GetResultsInFullPath(root)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        if (paths.Any(path => !path.StartsWith(rootWithSeparator, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Yarn manifest '{manifestPath}' resolved a source outside its directory.");
        }

        var before = paths.ToDictionary(path => path, CaptureMetadata, StringComparer.Ordinal);
        var files = new List<YarnSourceFileSnapshot>(paths.Length);
        foreach (var path in paths)
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var logicalPath = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(new YarnSourceFileSnapshot(
                logicalPath,
                path,
                content,
                Hash(Encoding.UTF8.GetBytes(content))));
        }

        if (manifestBefore != CaptureMetadata(manifestPath) ||
            before.Any(pair => pair.Value != CaptureMetadata(pair.Key)))
        {
            throw new YarnSourceChangedDuringReadException(
                $"Yarn sources for project '{projectId}' changed while being read.");
        }

        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(manifest.ProjectFileVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
        fingerprint.AppendData(new byte[] { 0 });
        fingerprint.AppendData(Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(manifest.BaseLanguage) ? "en" : manifest.BaseLanguage));
        fingerprint.AppendData(new byte[] { 0 });
        foreach (var file in files)
        {
            fingerprint.AppendData(Encoding.UTF8.GetBytes(file.LogicalPath));
            fingerprint.AppendData(new byte[] { 0 });
            fingerprint.AppendData(Encoding.UTF8.GetBytes(file.Content));
            fingerprint.AppendData(new byte[] { 0 });
        }

        return new YarnSourceSnapshot(
            projectId,
            manifestPath,
            manifest.ProjectFileVersion,
            string.IsNullOrWhiteSpace(manifest.BaseLanguage) ? "en" : manifest.BaseLanguage,
            files.AsReadOnly(),
            Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant());
    }

    private static string NormalizePattern(string pattern) =>
        pattern.Replace('\\', '/').TrimStart('/');

    private static FileMetadata CaptureMetadata(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Yarn source file was not found.", path);
        }
        return new FileMetadata(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Manifest(
        [property: JsonPropertyName("projectFileVersion")] int ProjectFileVersion,
        [property: JsonPropertyName("sourceFiles")] string[]? SourceFiles,
        [property: JsonPropertyName("excludeFiles")] string[]? ExcludeFiles,
        [property: JsonPropertyName("baseLanguage")] string? BaseLanguage);

    private readonly record struct FileMetadata(long Length, long LastWriteTicks);
}

public sealed class YarnSourceChangedDuringReadException : IOException
{
    public YarnSourceChangedDuringReadException(string message) : base(message)
    {
    }
}

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.State;

public partial class FileStateStorage : IStateStorage, IStateStorageDiagnostics, IRecoverableStateStorage, IDisposable
{
    private const string Extension = ".state";
    private readonly string rootPath;
    private readonly IStateStorageCommitObserver? observer;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private bool cleanupCompleted;
    private readonly object diagnosticSync = new();
    private string? lastKey;
    private StateStorageCommitStage? lastCommitStage;
    private string? lastFailureMessage;

    public FileStateStorage(
        string rootDirectory,
        string storageNamespace,
        IStateStorageCommitObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ValidateLogicalPath(storageNamespace, nameof(storageNamespace));
        rootPath = Path.GetFullPath(Path.Combine(
            rootDirectory,
            storageNamespace.Replace('/', Path.DirectorySeparatorChar)));
        this.observer = observer;
    }

    public async Task<byte[]> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        await EnsureCleanupAsync(cancellationToken);
        var gate = GetLock(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                throw new StateStorageNotFoundException(key);
            }

            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (StateRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StateStorageException($"Could not read state object '{key}'.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        await EnsureCleanupAsync(cancellationToken);
        var gate = GetLock(key);
        await gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            SetDiagnostic(key, null, null);
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            SetDiagnostic(key, StateStorageCommitStage.TemporaryFileFlushed, null);

            if (observer is not null)
            {
                await observer.ObserveAsync(
                    key,
                    StateStorageCommitStage.TemporaryFileFlushed,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
            SetDiagnostic(key, StateStorageCommitStage.Committed, null);

            if (observer is not null)
            {
                try
                {
                    await observer.ObserveAsync(
                        key,
                        StateStorageCommitStage.Committed,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // The commit point has passed. Observer failures are diagnostic only;
                    // reporting failure here would make the committed result ambiguous.
                    SetDiagnostic(
                        key,
                        StateStorageCommitStage.Committed,
                        exception.Message);
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            SetDiagnostic(key, LastCommitStage, exception.Message);
            throw;
        }
        catch (StateRuntimeException exception)
        {
            SetDiagnostic(key, LastCommitStage, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            SetDiagnostic(key, LastCommitStage, exception.Message);
            throw new StateStorageException($"Could not write state object '{key}'.", exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A later cleanup pass removes abandoned temporary files.
                }
            }

            gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureCleanupAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(rootPath, $"*{Extension}", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(rootPath, path))
            .Select(path => path[..^Extension.Length].Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        await EnsureCleanupAsync(cancellationToken);
        var gate = GetLock(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                throw new StateStorageNotFoundException(key);
            }

            File.Delete(path);
        }
        catch (StateRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StateStorageException($"Could not delete state object '{key}'.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StateStorageRecoveryResult> QuarantineAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        await EnsureCleanupAsync(cancellationToken);
        var gate = GetLock(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                throw new StateStorageNotFoundException(key);
            }

            var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var quarantinePath = path + $".corrupt-{suffix}";
            File.Move(path, quarantinePath);
            var quarantineKey = key + $".corrupt-{suffix}";
            return new StateStorageRecoveryResult(key, quarantineKey, GetBackupKey(key));
        }
        catch (StateRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StateStorageException(
                $"Could not quarantine state object '{key}'.",
                exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public string? GetBackupKey(string key)
    {
        var path = Resolve(key);
        return File.Exists(path + ".bak") ? key + ".bak" : null;
    }

    protected string StorageRootPath => rootPath;

    public string? LastKey { get { lock (diagnosticSync) { return lastKey; } } }
    public StateStorageCommitStage? LastCommitStage
    {
        get { lock (diagnosticSync) { return lastCommitStage; } }
    }
    public string? LastFailureMessage
    {
        get { lock (diagnosticSync) { return lastFailureMessage; } }
    }

    private SemaphoreSlim GetLock(string key) =>
        keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private void SetDiagnostic(
        string key,
        StateStorageCommitStage? stage,
        string? failureMessage)
    {
        lock (diagnosticSync)
        {
            lastKey = key;
            lastCommitStage = stage;
            lastFailureMessage = failureMessage;
        }
    }

    private string Resolve(string key)
    {
        ValidateLogicalPath(key, nameof(key));
        var relative = key.Replace('/', Path.DirectorySeparatorChar) + Extension;
        var path = Path.GetFullPath(Path.Combine(rootPath, relative));
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("State key escapes the storage namespace.", nameof(key));
        }

        return path;
    }

    private async Task EnsureCleanupAsync(CancellationToken cancellationToken)
    {
        if (cleanupCompleted)
        {
            return;
        }

        await cleanupGate.WaitAsync(cancellationToken);
        try
        {
            if (cleanupCompleted)
            {
                return;
            }

            if (Directory.Exists(rootPath))
            {
                foreach (var path in Directory.EnumerateFiles(
                             rootPath,
                             $"*{Extension}.tmp-*",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(path);
                }
            }

            cleanupCompleted = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new StateStorageException(
                "Could not clean abandoned state temporary files.",
                exception);
        }
        finally
        {
            cleanupGate.Release();
        }
    }

    private static void ValidateLogicalPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 240 ||
            value.StartsWith('/') ||
            value.EndsWith('/') ||
            !LogicalPathRegex().IsMatch(value) ||
            value.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "State paths may only contain non-empty alphanumeric, '.', '_' and '-' segments.",
                parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalPathRegex();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            cleanupGate.Dispose();
            foreach (var semaphore in keyLocks.Values)
            {
                semaphore.Dispose();
            }
            keyLocks.Clear();
        }
    }
}

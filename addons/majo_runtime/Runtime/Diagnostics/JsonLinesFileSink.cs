#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class JsonLinesFileSink : IRuntimeLogSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object writeGate = new();
    private readonly string logDirectory;
    private readonly string baseFileName;
    private readonly long maxFileSizeBytes;
    private readonly int maxFileCount;
    private readonly BlockingCollection<RuntimeLogEvent> queue = new(new ConcurrentQueue<RuntimeLogEvent>());
    private readonly Thread workerThread;
    private readonly CancellationTokenSource cancellation = new();
    private long currentFileSizeBytes;
    private StreamWriter? currentWriter;
    private string? currentFilePath;
    private bool isDisposed;

    public JsonLinesFileSink(
        string logDirectory,
        string baseFileName = "majo_runtime",
        long maxFileSizeBytes = 5 * 1024 * 1024,
        int maxFileCount = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseFileName);
        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "Max file size must be greater than zero.");
        }
        if (maxFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileCount), "Max file count must be greater than zero.");
        }

        this.logDirectory = logDirectory;
        this.baseFileName = baseFileName;
        this.maxFileSizeBytes = maxFileSizeBytes;
        this.maxFileCount = maxFileCount;

        workerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "Majo.Diagnostics.JsonLinesFileSink",
        };
        workerThread.Start();
    }

    public string LogDirectory => logDirectory;

    public void Emit(RuntimeLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (isDisposed || queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            queue.Add(logEvent);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Flush()
    {
        // Give background queue brief time to drain
        var spin = new SpinWait();
        var timeout = DateTime.UtcNow.AddMilliseconds(RuntimeDefaults.DiagnosticsFlushTimeoutMilliseconds);
        while (queue.Count > 0 && DateTime.UtcNow < timeout)
        {
            spin.SpinOnce();
        }

        lock (writeGate)
        {
            currentWriter?.Flush();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        try
        {
            queue.CompleteAdding();
            if (!workerThread.Join(TimeSpan.FromSeconds(RuntimeDefaults.DiagnosticsShutdownTimeoutSeconds)))
            {
                cancellation.Cancel();
                workerThread.Join(TimeSpan.FromMilliseconds(RuntimeDefaults.DiagnosticsForcedShutdownTimeoutMilliseconds));
            }
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
            lock (writeGate)
            {
                currentWriter?.Flush();
                currentWriter?.Dispose();
                currentWriter = null;
            }
        }
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var logEvent in queue.GetConsumingEnumerable(cancellation.Token))
            {
                WriteEvent(logEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining items on cancellation
            while (queue.TryTake(out var remainingEvent))
            {
                WriteEvent(remainingEvent);
            }
        }
        catch
        {
        }
    }

    private void WriteEvent(RuntimeLogEvent logEvent)
    {
        try
        {
            var dto = new JsonLogRecord
            {
                Timestamp = logEvent.Timestamp.ToString("O"),
                Level = logEvent.Level.ToString(),
                Category = logEvent.Category,
                EventId = logEvent.EventId,
                Message = logEvent.Message,
                Exception = logEvent.Exception?.ToString(),
                Correlation = logEvent.Correlation == default ? null : new JsonCorrelationRecord
                {
                    RuntimeId = logEvent.Correlation.RuntimeId,
                    SessionId = logEvent.Correlation.SessionId,
                    DialogueSessionId = logEvent.Correlation.DialogueSessionId,
                    CommandId = logEvent.Correlation.CommandId,
                    OperationId = logEvent.Correlation.OperationId,
                },
                Properties = logEvent.Properties.Count == 0 ? null : SerializeProperties(logEvent.Properties),
            };

            var json = JsonSerializer.Serialize(dto, JsonOptions);

            lock (writeGate)
            {
                EnsureWriter();
                if (currentWriter is null)
                {
                    return;
                }

                currentWriter.WriteLine(json);
                currentWriter.Flush();
                currentFileSizeBytes += json.Length + Environment.NewLine.Length;

                if (currentFileSizeBytes >= maxFileSizeBytes)
                {
                    RotateFilesLocked();
                }
            }
        }
        catch
        {
            // Sinks must never throw up or crash
        }
    }

    private static Dictionary<string, object?> SerializeProperties(IReadOnlyDictionary<string, object?> properties)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            result[key] = value switch
            {
                null => null,
                string s => s,
                bool b => b,
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
                _ => value.ToString(),
            };
        }
        return result;
    }

    private void EnsureWriter()
    {
        if (currentWriter is not null)
        {
            return;
        }

        Directory.CreateDirectory(logDirectory);
        currentFilePath = Path.Combine(logDirectory, $"{baseFileName}.jsonl");
        var fileInfo = new FileInfo(currentFilePath);
        currentFileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0;
        var stream = new FileStream(currentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        currentWriter = new StreamWriter(stream) { AutoFlush = true };
    }

    private void RotateFilesLocked()
    {
        currentWriter?.Flush();
        currentWriter?.Dispose();
        currentWriter = null;

        try
        {
            // Delete oldest if maxFileCount exceeded
            var oldestPath = Path.Combine(logDirectory, $"{baseFileName}.{maxFileCount - 1}.jsonl");
            if (File.Exists(oldestPath))
            {
                File.Delete(oldestPath);
            }

            // Shift existing archives
            for (var index = maxFileCount - 2; index >= 1; index--)
            {
                var src = Path.Combine(logDirectory, $"{baseFileName}.{index}.jsonl");
                var dst = Path.Combine(logDirectory, $"{baseFileName}.{index + 1}.jsonl");
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }

            // Rename primary file to .1.jsonl
            var primaryPath = Path.Combine(logDirectory, $"{baseFileName}.jsonl");
            if (File.Exists(primaryPath))
            {
                var firstArchive = Path.Combine(logDirectory, $"{baseFileName}.1.jsonl");
                File.Move(primaryPath, firstArchive, overwrite: true);
            }
        }
        catch
        {
        }

        currentFileSizeBytes = 0;
        EnsureWriter();
    }

    private sealed class JsonLogRecord
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public JsonCorrelationRecord? Correlation { get; set; }
        public Dictionary<string, object?>? Properties { get; set; }
    }

    private sealed class JsonCorrelationRecord
    {
        public string? RuntimeId { get; set; }
        public string? SessionId { get; set; }
        public string? DialogueSessionId { get; set; }
        public string? CommandId { get; set; }
        public string? OperationId { get; set; }
    }
}

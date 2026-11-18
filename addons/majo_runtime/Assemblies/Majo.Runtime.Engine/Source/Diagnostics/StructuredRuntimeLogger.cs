#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class StructuredRuntimeLogger : IRuntimeLogger, IRuntimeLogScopeProvider, IDisposable
{
    private readonly IRuntimeLogSink sink;
    private readonly Dictionary<string, RuntimeLogLevel> categoryOverrides;
    private readonly AsyncLocal<RuntimeLogCorrelation?> ambientCorrelation = new();
    private RuntimeLogLevel minimumLevel;
    private RuntimeLogCorrelation rootCorrelation;

    public StructuredRuntimeLogger(
        IRuntimeLogSink sink,
        RuntimeLogLevel minimumLevel = RuntimeLogLevel.Information,
        IReadOnlyDictionary<string, RuntimeLogLevel>? categoryOverrides = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.minimumLevel = minimumLevel;
        this.categoryOverrides = categoryOverrides is null
            ? new Dictionary<string, RuntimeLogLevel>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, RuntimeLogLevel>(categoryOverrides, StringComparer.OrdinalIgnoreCase);
    }

    public RuntimeLogLevel MinimumLevel
    {
        get => minimumLevel;
        set => minimumLevel = value;
    }

    public IReadOnlyDictionary<string, RuntimeLogLevel> CategoryOverrides => categoryOverrides;

    public IRuntimeLogSink Sink => sink;

    public RuntimeLogCorrelation CurrentCorrelation =>
        Merge(rootCorrelation, ambientCorrelation.Value ?? default);

    public void SetRootCorrelation(RuntimeLogCorrelation correlation)
    {
        rootCorrelation = correlation;
    }

    public IDisposable BeginScope(RuntimeLogCorrelation correlation)
    {
        var previous = ambientCorrelation.Value;
        ambientCorrelation.Value = Merge(CurrentCorrelation, correlation);
        return new CorrelationScope(this, previous);
    }

    public void SetCategoryLevel(string category, RuntimeLogLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        categoryOverrides[category] = level;
    }

    public void ClearCategoryLevel(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        categoryOverrides.Remove(category);
    }

    public bool IsEnabled(string category, RuntimeLogLevel level)
    {
        if (categoryOverrides.TryGetValue(category, out var overriddenLevel))
        {
            return level >= overriddenLevel;
        }

        // Support prefix matching (e.g. "core" matches "core.lifecycle")
        var dotIndex = category.LastIndexOf('.');
        while (dotIndex > 0)
        {
            var parent = category[..dotIndex];
            if (categoryOverrides.TryGetValue(parent, out var parentLevel))
            {
                return level >= parentLevel;
            }
            dotIndex = parent.LastIndexOf('.');
        }

        return level >= minimumLevel;
    }

    public void Log(RuntimeLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (!IsEnabled(logEvent.Category, logEvent.Level))
        {
            return;
        }

        sink.Emit(logEvent with
        {
            Correlation = Merge(CurrentCorrelation, logEvent.Correlation),
            Properties = SanitizeProperties(logEvent.Properties),
        });
    }

    public void Dispose() => sink.Dispose();

    private static RuntimeLogCorrelation Merge(
        RuntimeLogCorrelation parent,
        RuntimeLogCorrelation child) => new(
            string.IsNullOrWhiteSpace(child.RuntimeId) ? parent.RuntimeId : child.RuntimeId,
            child.SessionId ?? parent.SessionId,
            child.DialogueSessionId ?? parent.DialogueSessionId,
            child.CommandId ?? parent.CommandId);

    private static IReadOnlyDictionary<string, object?> SanitizeProperties(
        IReadOnlyDictionary<string, object?> properties)
    {
        if (properties.Count == 0)
        {
            return properties;
        }

        var sanitized = new Dictionary<string, object?>(
            properties.Count,
            StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            sanitized[key] = value switch
            {
                null => null,
                string or bool or byte or sbyte or short or ushort or int or uint or
                    long or ulong or float or double or decimal => value,
                Enum enumValue => enumValue.ToString(),
                _ => value.ToString(),
            };
        }

        return sanitized;
    }

    private sealed class CorrelationScope : IDisposable
    {
        private StructuredRuntimeLogger? owner;
        private readonly RuntimeLogCorrelation? previous;

        public CorrelationScope(
            StructuredRuntimeLogger owner,
            RuntimeLogCorrelation? previous)
        {
            this.owner = owner;
            this.previous = previous;
        }

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref owner, null);
            if (currentOwner is not null)
            {
                currentOwner.ambientCorrelation.Value = previous;
            }
        }
    }
}

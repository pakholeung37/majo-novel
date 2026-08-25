#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Core;

public enum RuntimeLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public interface IRuntimeLogger
{
    bool IsEnabled(string category, RuntimeLogLevel level);

    void Log(RuntimeLogEvent logEvent);
}

public interface IRuntimeLogScopeProvider
{
    RuntimeLogCorrelation CurrentCorrelation { get; }

    void SetRootCorrelation(RuntimeLogCorrelation correlation);

    IDisposable BeginScope(RuntimeLogCorrelation correlation);
}

public static class RuntimeLoggerExtensions
{
    public static IDisposable BeginCorrelationScope(
        this IRuntimeLogger logger,
        RuntimeLogCorrelation correlation) =>
        logger is IRuntimeLogScopeProvider scopeProvider
            ? scopeProvider.BeginScope(correlation)
            : EmptyRuntimeLogScope.Instance;

    public static void Log(
        this IRuntimeLogger logger,
        RuntimeLogLevel level,
        string category,
        string eventId,
        string message,
        Exception? exception = null,
        RuntimeLogCorrelation correlation = default,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(category, level))
        {
            return;
        }

        logger.Log(new RuntimeLogEvent(
            DateTimeOffset.UtcNow,
            level,
            category,
            eventId,
            message,
            exception,
            correlation,
            properties));
    }

    public static void Debug(
        this IRuntimeLogger logger,
        string category,
        string eventId,
        string message,
        RuntimeLogCorrelation correlation = default,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        logger.Log(RuntimeLogLevel.Debug, category, eventId, message, null, correlation, properties);

    public static void Info(
        this IRuntimeLogger logger,
        string category,
        string eventId,
        string message,
        RuntimeLogCorrelation correlation = default,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        logger.Log(RuntimeLogLevel.Information, category, eventId, message, null, correlation, properties);

    public static void Warning(
        this IRuntimeLogger logger,
        string category,
        string eventId,
        string message,
        Exception? exception = null,
        RuntimeLogCorrelation correlation = default,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        logger.Log(RuntimeLogLevel.Warning, category, eventId, message, exception, correlation, properties);

    public static void Error(
        this IRuntimeLogger logger,
        string category,
        string eventId,
        string message,
        Exception? exception = null,
        RuntimeLogCorrelation correlation = default,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        logger.Log(RuntimeLogLevel.Error, category, eventId, message, exception, correlation, properties);
}

internal sealed class EmptyRuntimeLogScope : IDisposable
{
    public static EmptyRuntimeLogScope Instance { get; } = new();

    public void Dispose()
    {
    }
}

public sealed class NullRuntimeLogger : IRuntimeLogger
{
    public static NullRuntimeLogger Instance { get; } = new();

    private NullRuntimeLogger()
    {
    }

    public bool IsEnabled(string category, RuntimeLogLevel level) => false;

    public void Log(RuntimeLogEvent logEvent)
    {
    }
}

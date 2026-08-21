#nullable enable

using System;

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
    void Log(RuntimeLogLevel level, string message, Exception? exception = null);
}

public sealed class NullRuntimeLogger : IRuntimeLogger
{
    public static NullRuntimeLogger Instance { get; } = new();

    private NullRuntimeLogger()
    {
    }

    public void Log(RuntimeLogLevel level, string message, Exception? exception = null)
    {
    }
}

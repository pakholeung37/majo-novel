#nullable enable

using System;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;

namespace Majo.Runtime;

public sealed class GodotRuntimeLogger : IRuntimeLogger
{
    private readonly GodotConsoleSink consoleSink = new();

    public bool IsEnabled(string category, RuntimeLogLevel level) => true;

    public void Log(RuntimeLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        consoleSink.Emit(logEvent);
    }
}

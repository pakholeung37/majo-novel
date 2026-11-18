#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class RecordingLogSink : IRuntimeLogSink
{
    private readonly object gate = new();
    private readonly List<RuntimeLogEvent> events = new();

    public IReadOnlyList<RuntimeLogEvent> Events
    {
        get
        {
            lock (gate)
            {
                return events.ToArray();
            }
        }
    }

    public void Emit(RuntimeLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (gate)
        {
            events.Add(logEvent);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            events.Clear();
        }
    }

    public void Dispose()
    {
    }
}

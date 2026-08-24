#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class RingBufferLogSink : IRuntimeLogSink
{
    private readonly object gate = new();
    private readonly Queue<RuntimeLogEvent> buffer;
    private readonly int capacity;

    public RingBufferLogSink(int capacity = 500)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        this.capacity = capacity;
        buffer = new Queue<RuntimeLogEvent>(capacity);
    }

    public int Capacity => capacity;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return buffer.Count;
            }
        }
    }

    public IReadOnlyList<RuntimeLogEvent> RecentEvents
    {
        get
        {
            lock (gate)
            {
                return buffer.ToArray();
            }
        }
    }

    public void Emit(RuntimeLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (gate)
        {
            if (buffer.Count >= capacity)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(logEvent);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            buffer.Clear();
        }
    }

    public void Dispose()
    {
    }
}

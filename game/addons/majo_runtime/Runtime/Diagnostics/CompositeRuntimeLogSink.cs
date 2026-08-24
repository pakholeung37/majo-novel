#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class CompositeRuntimeLogSink : IRuntimeLogSink
{
    private readonly IReadOnlyList<IRuntimeLogSink> sinks;
    private bool isDisposed;

    public CompositeRuntimeLogSink(IReadOnlyList<IRuntimeLogSink> sinks)
    {
        this.sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
    }

    public IReadOnlyList<IRuntimeLogSink> Sinks => sinks;

    public void Emit(RuntimeLogEvent logEvent)
    {
        if (isDisposed)
        {
            return;
        }

        for (var index = 0; index < sinks.Count; index++)
        {
            try
            {
                sinks[index].Emit(logEvent);
            }
            catch
            {
                // Composite sink guarantees complete failure isolation across sinks.
            }
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        for (var index = 0; index < sinks.Count; index++)
        {
            try
            {
                sinks[index].Dispose();
            }
            catch
            {
            }
        }
    }
}

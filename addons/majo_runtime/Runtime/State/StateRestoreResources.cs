#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Majo.Runtime.Core;
using Majo.Runtime.Input;

namespace Majo.Runtime.State;

/// <summary>One owner for the leases retained during restore or transferred into a fault.</summary>
internal sealed class StateRestoreResources : IDisposable
{
    public IRetainedRestoreOutput? Output { get; set; }
    public IInputHandle? InputMuter { get; set; }
    public IDisposable? PlaybackLease { get; set; }
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var output = Output;
        var input = InputMuter;
        var playback = PlaybackLease;
        Output = null;
        InputMuter = null;
        PlaybackLease = null;
        List<Exception>? failures = null;
        Release(output);
        Release(input);
        Release(playback);
        if (failures is not null) throw new AggregateException("Restore lease cleanup failed.", failures);
        void Release(IDisposable? lease)
        {
            try { lease?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
    }
}

#nullable enable

using System;
using System.Threading;

namespace Majo.Runtime.Actors;

/// <summary>One owner plus consumer leases. Closing the owner rejects new leases;
/// the release callback runs once, after all existing consumers have detached.</summary>
public sealed class BackgroundContentLifetime : IDisposable
{
    private readonly Action release;
    private readonly object gate = new();

    public BackgroundContentLifetime(Action release) =>
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    private int references = 1;
    private bool closed;

    public IDisposable Retain()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            checked { references++; }
            return new Lease(this);
        }
    }

    public void Dispose()
    {
        bool last;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            last = --references == 0;
        }
        if (last) release();
    }

    private void Release()
    {
        bool last;
        lock (gate) last = --references == 0;
        if (last) release();
    }

    private sealed class Lease(BackgroundContentLifetime owner) : IDisposable
    {
        private BackgroundContentLifetime? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}

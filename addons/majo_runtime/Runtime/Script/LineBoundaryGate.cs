#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Script;

/// <summary>Serializes line preparation and coalesces input without cancelling its work.</summary>
public sealed class LineBoundaryGate : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource session = new();
    private bool preparing;
    private bool restoring;
    private bool pendingAdvance;
    private bool pendingHurryUp;
    private long generation;
    private long occurrence;
    private TaskCompletionSource? preparation;

    public Task WaitForPreparationAsync(CancellationToken token)
    {
        lock (sync) return preparation?.Task.WaitAsync(token) ?? Task.CompletedTask;
    }

    public bool IsPreparing { get { lock (sync) return preparing; } }

    public void BeginSession()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            previous = session;
            preparation?.TrySetCanceled();
            preparation = null;
            session = new CancellationTokenSource();
            generation++;
            occurrence = 0;
            preparing = pendingAdvance = pendingHurryUp = false;
        }
        previous.Cancel();
        previous.Dispose();
    }

    public async Task<IDisposable> AcquireRestoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CancellationTokenSource current;
        Task pending;
        lock (sync)
        {
            if (restoring) throw new InvalidOperationException("Playback already belongs to another restore.");
            restoring = true;
            pendingAdvance = pendingHurryUp = false;
            current = session;
            pending = preparation?.Task ?? Task.CompletedTask;
        }
        try
        {
            current.Cancel();
            // Drain the cancelled owner before taking State's transaction gate.
            await pending;
            return new RestoreLease(this);
        }
        catch
        {
            lock (sync) restoring = false;
            throw;
        }
    }

    private sealed class RestoreLease(LineBoundaryGate owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (owner.sync) owner.restoring = false;
        }
    }

    public void Stop()
    {
        CancellationTokenSource current;
        lock (sync)
        {
            current = session;
            pendingAdvance = pendingHurryUp = false;
        }
        current.Cancel();
    }

    public Lease Enter(bool restoreReplay = false)
    {
        lock (sync)
        {
            session.Token.ThrowIfCancellationRequested();
            if (restoring && !restoreReplay)
                throw new InvalidOperationException("Normal playback cannot enter a line during state replacement.");
            if (preparing) throw new InvalidOperationException("A line boundary is already preparing.");
            preparing = true;
            preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingAdvance = pendingHurryUp = false;
            return new Lease(this, generation, ++occurrence, session.Token);
        }
    }

    public bool DeferAdvance(bool hurryUp = false)
    {
        lock (sync)
        {
            if (restoring) return true;
            if (!preparing) return false;
            if (hurryUp) pendingHurryUp = true;
            else pendingAdvance = true;
            return true;
        }
    }

    private void Complete(Lease lease, Action? advance, Action? hurryUp)
    {
        bool next;
        bool hurry;
        lock (sync)
        {
            if (lease.Generation != generation || lease.Sequence != occurrence) return;
            next = pendingAdvance && !lease.Token.IsCancellationRequested;
            hurry = pendingHurryUp && !lease.Token.IsCancellationRequested;
            preparing = pendingAdvance = pendingHurryUp = false;
            preparation?.TrySetResult();
            preparation = null;
        }
        if (hurry) hurryUp?.Invoke();
        if (next) advance?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        session.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly LineBoundaryGate owner;
        private bool completed;

        internal Lease(LineBoundaryGate owner, long generation, long sequence, CancellationToken token)
        {
            this.owner = owner;
            Generation = generation;
            Sequence = sequence;
            Token = token;
        }

        public long Generation { get; }
        public long Sequence { get; }
        public CancellationToken Token { get; }

        /// <summary>Called only after all presenters have received the committed line.</summary>
        public void Activate(Action advance, Action hurryUp)
        {
            Token.ThrowIfCancellationRequested();
            if (completed) throw new InvalidOperationException("Line boundary has already completed.");
            completed = true;
            owner.Complete(this, advance, hurryUp);
        }

        public void Dispose()
        {
            if (completed) return;
            completed = true;
            owner.Complete(this, null, null);
        }
    }
}

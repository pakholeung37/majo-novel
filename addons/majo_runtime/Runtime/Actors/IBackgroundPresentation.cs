#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.State;

namespace Majo.Runtime.Actors;

public enum BackgroundCoverage { Unknown, OpaqueRectangle }

/// <summary>Background content in native local coordinates. Layout belongs to the host.</summary>
public interface IBackgroundContent
{
    /// <summary>Native content root. Consumers mount it under their own layout node.
    /// The actor must detach this node before freeing its lifecycle root while leases exist.</summary>
    CanvasItem Root { get; }
    /// <summary>Immutable finite bounds with positive dimensions; the origin may be nonzero.</summary>
    Rect2 LocalBounds { get; }
    BackgroundCoverage Coverage { get; }
    /// <summary>Keeps both nodes and resources alive. New leases are rejected once the owner closes.</summary>
    // A lease preserves live rendering and animation, even after appearance confirmation retires
    // this content. Keep content-specific simulation with its root; do not stop it on replacement.
    // Once mounted by a host, visibility/layout belong to that host.
    IDisposable Retain();
}

/// <summary>A prepared appearance keeps its source alive until confirmation or rollback.
/// Dispose rolls back an unconfirmed commit. Commit and Rollback perform no resource I/O.</summary>
public interface IPreparedBackgroundAppearance : IAsyncDisposable
{
    string ResolvedAppearance { get; }
    IBackgroundContent Target { get; }
    void Commit();
    void Rollback();
    /// <summary>Ends rollback rights and releases the source owner; existing source leases remain valid.</summary>
    void ConfirmCommit();
}

public interface IBackgroundActor : IActor, IGodotActorView, IStateMutationParticipant
{
    IBackgroundContent? CurrentContent { get; }
    /// <summary>Synchronous notification used by hosts to bind and lay out stable content.
    /// Hosts must support another notification when a commit is rolled back.</summary>
    event Action? ContentChanged;
    Task<IPreparedBackgroundAppearance> PrepareBackgroundAppearanceAsync(
        string resolvedAppearance, CancellationToken cancellationToken = default);
}

/// <summary>Presentation ownership hooks. Candidate actors may attach before publication.</summary>
public interface IActorPresentationHost
{
    void Attach(IActor actor);
    void Detach(IActor actor);
}

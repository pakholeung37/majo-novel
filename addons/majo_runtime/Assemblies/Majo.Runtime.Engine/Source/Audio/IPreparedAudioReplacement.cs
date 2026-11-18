#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Audio;

public sealed record BgmRestoreTarget(string ClipId, string TrackId, bool Loop, float Volume);

public interface IAudioReplacementSource
{
    IReadOnlyList<BgmRestoreTarget> CaptureCommittedBgm();
    Task StopExecutionForRestoreAsync(CancellationToken cancellationToken);
    Task<IPreparedAudioReplacement> PrepareReplacementAsync(
        IReadOnlyList<BgmRestoreTarget> tracks, CancellationToken cancellationToken);
}

public interface IPreparedAudioReplacement : IAsyncDisposable
{
    void Commit();
}

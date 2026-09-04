#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public enum RuntimeActivityOwner
{
    Dialogue,
    Direction,
    Audio,
    Other,
}

public sealed record RuntimeActivityInfo(
    string DiagnosticId,
    RuntimeActivityOwner Owner,
    string Type,
    bool IsCancelable,
    bool IsFinishable,
    TimeSpan Elapsed,
    string? Source = null,
    bool IsIgnorable = false);

public interface IRuntimeActivityObserver : IRuntimeService
{
    IReadOnlyList<RuntimeActivityInfo> Snapshot();

    bool HasActiveActivities(RuntimeActivityOwner owner, bool includeIgnorable = false);

    bool HasActiveActivities(bool includeIgnorable = false);

    Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeActivityRegistry : IRuntimeActivityObserver
{
    IDisposable BeginActivity(
        RuntimeActivityOwner owner,
        string type,
        string? source = null,
        bool isCancelable = true,
        bool isFinishable = true,
        bool isIgnorable = false);
}

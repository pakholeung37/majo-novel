#nullable enable

using System.Threading;

namespace Majo.Runtime.Operations;

public sealed class OperationContext
{
    internal OperationContext(
        IFrameClock frameClock,
        CancellationToken cancellationToken,
        string? id,
        string? groupId)
    {
        FrameClock = frameClock;
        CancellationToken = cancellationToken;
        Id = id;
        GroupId = groupId;
    }

    public IFrameClock FrameClock { get; }

    public CancellationToken CancellationToken { get; }

    public string? Id { get; }

    public string? GroupId { get; }
}

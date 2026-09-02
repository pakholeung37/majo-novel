#nullable enable

using System.Threading;

namespace Majo.Runtime.Operations;

public sealed class OperationContext
{
    internal OperationContext(
        IFrameClock frameClock,
        string? id,
        string? groupId,
        CancellationToken cancellationToken)
    {
        FrameClock = frameClock;
        Id = id;
        GroupId = groupId;
        CancellationToken = cancellationToken;
    }

    public IFrameClock FrameClock { get; }

    public CancellationToken CancellationToken { get; }

    public string? Id { get; }

    public string? GroupId { get; }
}

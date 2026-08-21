#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public interface IOperationHandle
{
    string? Id { get; }

    string? GroupId { get; }

    OperationState State { get; }

    Task Completion { get; }

    void Cancel();

    void Complete();
}

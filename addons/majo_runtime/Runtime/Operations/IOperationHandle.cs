#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public interface IOperationHandle
{
    OperationState State { get; }

    Task Completion { get; }

    void Finish();

    void Cancel();
}

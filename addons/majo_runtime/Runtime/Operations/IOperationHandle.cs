#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public interface IOperationHandle
{
    public static IOperationHandle Completed => CompletedOperationHandle.Instance;

    OperationState State { get; }

    Task Completion { get; }

    void Finish();

    void Cancel();
}

public sealed class CompletedOperationHandle : IOperationHandle
{
    public static CompletedOperationHandle Instance { get; } = new();

    public OperationState State => OperationState.Completed;

    public Task Completion => Task.CompletedTask;

    public void Finish() { }

    public void Cancel() { }
}

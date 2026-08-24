#nullable enable

using System;
using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public interface IOperationHandle
{
    string? Id { get; }

    string? GroupId { get; }

    string DiagnosticId => Id ?? string.Empty;

    OperationState State { get; }

    TimeSpan Elapsed => TimeSpan.Zero;

    Task Completion { get; }

    void Cancel();

    void Complete();
}

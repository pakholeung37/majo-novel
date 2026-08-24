#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.State;

public interface IStateSerializer : IRuntimeService
{
    byte[] Serialize(StateEnvelope envelope);

    StateEnvelope Deserialize(System.ReadOnlySpan<byte> bytes);

    Task IRuntimeService.InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IRuntimeService.ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IRuntimeService.ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

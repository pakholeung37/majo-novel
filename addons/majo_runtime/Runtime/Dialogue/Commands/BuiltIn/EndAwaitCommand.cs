#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand("endAwait", Category = "Playback", Usage = "endAwait")]
public sealed class EndAwaitCommandArguments
{
}

public sealed class EndAwaitCommandHandler
    : IRuntimeCommandHandler<EndAwaitCommandArguments>
{
    public Task ExecuteAsync(
        EndAwaitCommandArguments arguments,
        RuntimeCommandContext context) => context.EndAwaitAsync();
}

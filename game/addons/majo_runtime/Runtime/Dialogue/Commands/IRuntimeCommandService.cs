#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommandService
{
    Task BackAsync(BackCommand command);

    Task CharacterAsync(CharacterCommand command);

    Task HideAsync(HideCommand command);

    Task AwaitAsync(AwaitCommand command);

    Task CancelAsync(CancelCommand command);
}

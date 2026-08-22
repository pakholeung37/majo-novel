#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommandService
{
    Task BackAsync(BackCommand command);

    Task BackAsync(BackCommand command, RuntimeCommandContext context) => BackAsync(command);

    Task CharacterAsync(CharacterCommand command);

    Task CharacterAsync(CharacterCommand command, RuntimeCommandContext context) =>
        CharacterAsync(command);

    Task HideAsync(HideCommand command);

    Task HideAsync(HideCommand command, RuntimeCommandContext context) => HideAsync(command);

    Task AwaitAsync(AwaitCommand command);

    Task AwaitAsync(AwaitCommand command, RuntimeCommandContext context) => AwaitAsync(command);

    Task CancelAsync(CancelCommand command);

    Task CancelAsync(CancelCommand command, RuntimeCommandContext context) => CancelAsync(command);
}

#nullable enable

using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IActorCommandService
{
    Task BackAsync(BackCommand command);

    Task BackAsync(BackCommand command, RuntimeCommandContext context) => BackAsync(command);

    Task CharacterAsync(CharacterCommand command);

    Task CharacterAsync(CharacterCommand command, RuntimeCommandContext context) =>
        CharacterAsync(command);

    Task HideAsync(HideCommand command);

    Task HideAsync(HideCommand command, RuntimeCommandContext context) => HideAsync(command);
}

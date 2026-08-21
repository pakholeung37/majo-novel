#nullable enable

using System;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class DialogueRunnerCommandRegistry : IYarnCommandRegistry
{
    private readonly DialogueRunner dialogueRunner;

    public DialogueRunnerCommandRegistry(DialogueRunner dialogueRunner)
    {
        this.dialogueRunner = dialogueRunner ??
            throw new ArgumentNullException(nameof(dialogueRunner));
        if (!GodotObject.IsInstanceValid(dialogueRunner))
        {
            throw new ArgumentException(
                "DialogueRunner must be a valid Godot instance.",
                nameof(dialogueRunner));
        }
    }

    public void AddCommandHandler(string commandName, Delegate handler) =>
        dialogueRunner.AddCommandHandler(commandName, handler);

    public void RemoveCommandHandler(string commandName) =>
        dialogueRunner.RemoveCommandHandler(commandName);
}

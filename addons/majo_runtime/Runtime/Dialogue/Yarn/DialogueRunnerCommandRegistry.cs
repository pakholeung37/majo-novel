#nullable enable

using System;
using System.Threading;
using Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class DialogueRunnerCommandRegistry : IYarnCommandRegistry
{
    private readonly DialogueRunner dialogueRunner;
    private readonly Callable dialogueStarted;
    private readonly Callable dialogueCompleted;
    private readonly Callable unhandledCommand;
    private CancellationTokenSource dialogueCancellation = new();
    private bool isDisposed;

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

        dialogueStarted = Callable.From(BeginDialogue);
        dialogueCompleted = Callable.From(CompleteDialogue);
        unhandledCommand = Callable.From<string>(HandleUnhandledCommand);
        dialogueRunner.Connect(DialogueRunner.SignalName.onDialogueStart, dialogueStarted);
        dialogueRunner.Connect(DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted);
        dialogueRunner.Connect(DialogueRunner.SignalName.onUnhandledCommand, unhandledCommand);
    }

    public event Action<string>? UnhandledCommand;

    public CancellationToken DialogueCancellationToken => dialogueCancellation.Token;

    public void AddCommandHandler(string commandName, Delegate handler) =>
        dialogueRunner.AddCommandHandler(commandName, handler);

    public void RemoveCommandHandler(string commandName) =>
        dialogueRunner.RemoveCommandHandler(commandName);

    public void AbortDialogue() => dialogueRunner.StopForget();

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        CancelDialogue();
        dialogueCancellation.Dispose();
        if (!GodotObject.IsInstanceValid(dialogueRunner))
        {
            return;
        }

        if (dialogueRunner.IsConnected(
                DialogueRunner.SignalName.onDialogueStart, dialogueStarted))
        {
            dialogueRunner.Disconnect(
                DialogueRunner.SignalName.onDialogueStart, dialogueStarted);
        }

        if (dialogueRunner.IsConnected(
                DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted))
        {
            dialogueRunner.Disconnect(
                DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted);
        }

        if (dialogueRunner.IsConnected(
                DialogueRunner.SignalName.onUnhandledCommand, unhandledCommand))
        {
            dialogueRunner.Disconnect(
                DialogueRunner.SignalName.onUnhandledCommand, unhandledCommand);
        }
    }

    private void BeginDialogue()
    {
        CancelDialogue();
        dialogueCancellation.Dispose();
        dialogueCancellation = new CancellationTokenSource();
    }

    private void CompleteDialogue() => CancelDialogue();

    private async void HandleUnhandledCommand(string commandText)
    {
        UnhandledCommand?.Invoke(commandText);

        // YarnSpinner-Godot returns immediately from its unknown-command branch
        // before reaching the normal next-frame continuation. Resume explicitly so
        // an unregistered authoring command behaves as an error-and-skip operation.
        await YarnTask.NextFrame();
        if (!isDisposed &&
            GodotObject.IsInstanceValid(dialogueRunner) &&
            dialogueRunner.IsDialogueRunning &&
            !dialogueCancellation.IsCancellationRequested)
        {
            dialogueRunner.Dialogue.Continue();
        }
    }

    public void CancelDialogue()
    {
        if (!dialogueCancellation.IsCancellationRequested)
        {
            dialogueCancellation.Cancel();
        }
    }
}

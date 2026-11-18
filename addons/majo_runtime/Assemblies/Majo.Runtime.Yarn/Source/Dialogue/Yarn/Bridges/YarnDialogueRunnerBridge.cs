#nullable enable

// Portions adapted from YarnSpinner-Godot (MIT), copyright 2020 Secret Lab Pty Ltd
// and Yarn Spinner Contributors. See YarnSpinner-LICENSE.md in this directory.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Yarn;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Bridges;

/// <summary>
/// Version-pinned callback adapter for the unmodified Godot Runner. It drains cancelled
/// presentations before upstream cleanup and prevents old commands from advancing a new session.
/// </summary>
internal sealed class YarnDialogueRunnerBridge : IDisposable
{
    private readonly DialogueRunner runner;
    private readonly LineHandler? originalLine;
    private readonly OptionsHandler? originalOptions;
    private readonly CommandHandler? originalCommand;
    private readonly DialogueCompleteHandler? originalComplete;
    private readonly HashSet<Task> presentations = new();
    private bool disposed;

    public YarnDialogueRunnerBridge(DialogueRunner runner)
    {
        this.runner = runner;
        ValidateLayout();
        var dialogue = runner.Dialogue;
        originalLine = dialogue.LineHandler;
        originalOptions = dialogue.OptionsHandler;
        originalCommand = dialogue.CommandHandler;
        originalComplete = dialogue.DialogueCompleteHandler;
        dialogue.LineHandler = ReceiveLine;
        dialogue.OptionsHandler = ReceiveOptions;
        dialogue.CommandHandler = ReceiveCommand;
        dialogue.DialogueCompleteHandler = Complete;
    }

    private CancellationTokenSource? Session => PrivateAccess.Session(runner);

    private bool IsCurrent(CancellationTokenSource? session) =>
        !disposed && GodotObject.IsInstanceValid(runner) &&
        session?.IsCancellationRequested == false && ReferenceEquals(session, Session);

    private void ReceiveLine(Line line) => TrackPresentation(() => RunLineAsync(line)).Forget();

    private void ReceiveOptions(OptionSet options) =>
        TrackPresentation(() => PrivateAccess.RunOptionsAsync(runner, options)).Forget();

    private async YarnTask TrackPresentation(Func<YarnTask> run)
    {
        // Register before invoking the callback: it can synchronously request Stop.
        var retired = new TaskCompletionSource();
        presentations.Add(retired.Task);
        try { await run(); }
        catch (OperationCanceledException) when (Session?.IsCancellationRequested != false) { }
        finally
        {
            presentations.Remove(retired.Task);
            retired.TrySetResult();
        }
    }

    private async YarnTask RunLineAsync(Line line)
    {
        var session = Session;
        var localized = await runner.LineProvider.GetLocalizedLineAsync(
            line, session?.Token ?? CancellationToken.None);
        if (!IsCurrent(session)) return;
        localized.Source = runner;
        if (localized == LocalizedLine.InvalidLine)
            GD.PushError($"Failed to get a localised line for {line.ID}!");
        await PrivateAccess.RunLocalisedLine(runner, localized);
        if (IsCurrent(session)) runner.Dialogue.Continue();
    }

    private void ReceiveCommand(Command command) => RunCommandAsync(command).Forget();

    private async YarnTask RunCommandAsync(Command command)
    {
        var session = Session;
        var result = runner.CommandDispatcher.DispatchCommand(command.Text, runner);
        var parts = DialogueRunner.SplitCommandText(command.Text).ToArray();
        var name = parts.FirstOrDefault() ?? string.Empty;
        switch (result.Status)
        {
            case CommandDispatchResult.StatusType.Succeeded:
                await result.Task;
                break;
            case CommandDispatchResult.StatusType.CommandUnknown:
                var connections = runner.GetSignalConnectionList(DialogueRunner.SignalName.onUnhandledCommand)
                    .Where(connection => connection.ContainsKey("callable"))
                    .Select(connection => connection["callable"].AsCallable())
                    .Any(handler => !(handler.Target == runner && handler.Delegate is null));
                if (connections)
                    runner.EmitSignal(DialogueRunner.SignalName.onUnhandledCommand, command.Text);
                else
                    GD.PushError($"No Command \"{name}\" was found. Did you remember to use the YarnCommand attribute or AddCommandHandler() function in C#?");
                return;
            case CommandDispatchResult.StatusType.NoTargetFound:
                GD.PushError($"Can't call command {name}: failed to find a node named {parts.ElementAtOrDefault(1)}", runner);
                break;
            case CommandDispatchResult.StatusType.TargetMissingComponent:
                GD.PushError($"Can't call command {name}, because {parts.ElementAtOrDefault(1)} doesn't have the correct component");
                break;
            case CommandDispatchResult.StatusType.InvalidParameterCount:
                GD.PushError($"Can't call command {name}: incorrect number of parameters");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), $"Internal error: Unknown command dispatch result status {result}");
        }
        if (!IsCurrent(session)) return;
        await YarnTask.NextFrame();
        if (IsCurrent(session)) runner.Dialogue.Continue();
    }

    private void Complete() => CompleteAsync().Forget();

    private async YarnTask CompleteAsync()
    {
        if (Session?.IsCancellationRequested == true)
            await Task.WhenAll(presentations.ToArray());
        if (!disposed && GodotObject.IsInstanceValid(runner))
            await PrivateAccess.CompleteAsync(runner);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!GodotObject.IsInstanceValid(runner)) return;
        var dialogue = runner.Dialogue;
        if (dialogue.LineHandler == ReceiveLine) dialogue.LineHandler = originalLine;
        if (dialogue.OptionsHandler == ReceiveOptions) dialogue.OptionsHandler = originalOptions;
        if (dialogue.CommandHandler == ReceiveCommand) dialogue.CommandHandler = originalCommand;
        if (dialogue.DialogueCompleteHandler == Complete) dialogue.DialogueCompleteHandler = originalComplete;
    }

    private static void ValidateLayout()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(DialogueRunner);
        if (type.GetField("dialogueCancellationSource", flags)?.FieldType != typeof(CancellationTokenSource))
            throw new InvalidOperationException("Yarn Godot Runner bridge requires dialogueCancellationSource (CancellationTokenSource).");
        foreach (var (name, parameter) in new[]
        {
            ("RunLocalisedLine", typeof(LocalizedLine)),
            ("OnOptionsReceivedAsync", typeof(OptionSet)),
            ("OnDialogueCompleteAsync", (Type?)null),
        })
        {
            var method = type.GetMethod(name, flags, parameter is null ? Type.EmptyTypes : new[] { parameter });
            if (method?.ReturnType != typeof(YarnTask))
                throw new InvalidOperationException($"Yarn Godot Runner bridge requires {name} returning YarnTask.");
        }
    }

    private static class PrivateAccess
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dialogueCancellationSource")]
        internal static extern ref CancellationTokenSource? Session(DialogueRunner runner);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RunLocalisedLine")]
        internal static extern YarnTask RunLocalisedLine(DialogueRunner runner, LocalizedLine line);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnOptionsReceivedAsync")]
        internal static extern YarnTask RunOptionsAsync(DialogueRunner runner, OptionSet options);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnDialogueCompleteAsync")]
        internal static extern YarnTask CompleteAsync(DialogueRunner runner);
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using YarnSpinnerGodot;
using MajoDialogueOption = Majo.Runtime.Dialogue.DialogueOption;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed partial class YarnDialoguePresenterAdapter : Node, DialoguePresenterBase
{
    private readonly IReadOnlyList<IDialoguePresenter> presenters;
    private readonly Action requestHurryUp;
    private readonly Action requestNextLine;
    private readonly Action<DialogueLine, IReadOnlyList<string>> linePresented;
    private readonly Action<IReadOnlyList<MajoDialogueOption>> optionsPresented;
    private readonly object stopGate = new();
    private TaskCompletionSource stopBoundary = NewStopBoundary();
    private bool stopRequested;
    private int activePresentations;

    public YarnDialoguePresenterAdapter(
        IReadOnlyList<IDialoguePresenter> presenters,
        Action requestHurryUp,
        Action requestNextLine,
        Action<DialogueLine, IReadOnlyList<string>> linePresented,
        Action<IReadOnlyList<MajoDialogueOption>> optionsPresented)
    {
        this.presenters = presenters ?? throw new ArgumentNullException(nameof(presenters));
        this.requestHurryUp = requestHurryUp ??
            throw new ArgumentNullException(nameof(requestHurryUp));
        this.requestNextLine = requestNextLine ??
            throw new ArgumentNullException(nameof(requestNextLine));
        this.linePresented = linePresented ??
            throw new ArgumentNullException(nameof(linePresented));
        this.optionsPresented = optionsPresented ??
            throw new ArgumentNullException(nameof(optionsPresented));
    }

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public void BeginSession()
    {
        lock (stopGate)
        {
            stopRequested = false;
            activePresentations = 0;
            stopBoundary = NewStopBoundary();
        }
    }

    public Task PrepareToStop()
    {
        lock (stopGate)
        {
            stopRequested = true;
            if (activePresentations > 0)
            {
                stopBoundary.TrySetResult();
            }

            return stopBoundary.Task;
        }
    }

    public async YarnTask OnDialogueStartedAsync()
    {
        foreach (var presenter in presenters)
        {
            await presenter.OnDialogueStartedAsync(CancellationToken.None);
        }
    }

    public async YarnTask RunLineAsync(
        LocalizedLine line,
        LineCancellationToken token)
    {
        if (EnterPresentation())
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token.NextLineToken);
            }
            catch (OperationCanceledException) when (token.NextLineToken.IsCancellationRequested)
            {
            }
            finally
            {
                ExitPresentation();
            }

            return;
        }

        var converted = YarnLineConverter.Convert(line);
        try
        {
            linePresented(converted, Array.AsReadOnly(line.Substitutions.ToArray()));
            var context = new DialoguePresentationContext(
                token.HurryUpToken,
                token.NextLineToken,
                requestHurryUp,
                requestNextLine);
            var presentation = Task.WhenAll(presenters.Select(
                presenter => presenter.PresentLineAsync(converted, context)));
            try
            {
                await presentation.WaitAsync(token.NextLineToken);
            }
            catch (OperationCanceledException) when (token.NextLineToken.IsCancellationRequested)
            {
                _ = ObserveDetachedAsync(new[] { presentation });
            }
        }
        finally
        {
            ExitPresentation();
        }
    }

    public async YarnTask<YarnDialogueOption?> RunOptionsAsync(
        YarnDialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        if (EnterPresentation())
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                ExitPresentation();
            }

            return await DialogueRunner.NoOptionSelected;
        }

        try
        {
            return await RunOptionsCoreAsync(dialogueOptions, cancellationToken);
        }
        finally
        {
            ExitPresentation();
        }
    }

    private async YarnTask<YarnDialogueOption?> RunOptionsCoreAsync(
        YarnDialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        var converted = dialogueOptions.Select(YarnLineConverter.Convert).ToArray();
        optionsPresented(converted);
        if (presenters.Count == 0)
        {
            return await DialogueRunner.NoOptionSelected;
        }

        using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var pending = presenters.Select(async presenter =>
        {
            try
            {
                return await presenter.PresentOptionsAsync(
                    converted,
                    selectionCancellation.Token);
            }
            catch (OperationCanceledException) when (selectionCancellation.IsCancellationRequested)
            {
                return null;
            }
        }).ToList();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(
                pending.Cast<Task>().Append(cancellation));
            if (ReferenceEquals(completed, cancellation))
            {
                selectionCancellation.Cancel();
                _ = ObserveDetachedAsync(pending);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var completedTask = (Task<MajoDialogueOption?>)completed;
            pending.Remove(completedTask);
            var selection = await completedTask;
            if (selection is null)
            {
                continue;
            }

            var selected = dialogueOptions.FirstOrDefault(
                option => option.DialogueOptionID == selection.Id);
            if (selected is null || !selected.IsAvailable)
            {
                throw new DialogueException(
                    $"Presenter selected unknown or unavailable option '{selection.Id}'.");
            }

            selectionCancellation.Cancel();
            _ = ObserveDetachedAsync(pending);
            return selected;
        }

        return await DialogueRunner.NoOptionSelected;
    }

    public async YarnTask OnDialogueCompleteAsync()
    {
        SignalStopBoundary();
        foreach (var presenter in presenters)
        {
            await presenter.OnDialogueCompletedAsync(CancellationToken.None);
        }
    }

    public void OnNodeEnter(string nodeName)
    {
        foreach (var presenter in presenters)
        {
            presenter.OnNodeEntered(nodeName);
        }
    }

    public void OnNodeExit(string nodeName)
    {
        foreach (var presenter in presenters)
        {
            presenter.OnNodeExited(nodeName);
        }
    }

    private static async Task ObserveDetachedAsync(IEnumerable<Task> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                GD.PushError($"Detached dialogue presenter failed: {exception}");
            }
        }
    }

    private bool EnterPresentation()
    {
        lock (stopGate)
        {
            activePresentations++;
            if (stopRequested)
            {
                stopBoundary.TrySetResult();
                return true;
            }

            return false;
        }
    }

    private void ExitPresentation()
    {
        lock (stopGate)
        {
            activePresentations--;
        }
    }

    private void SignalStopBoundary()
    {
        lock (stopGate)
        {
            stopBoundary.TrySetResult();
        }
    }

    private static TaskCompletionSource NewStopBoundary() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}

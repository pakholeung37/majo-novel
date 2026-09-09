#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using YarnSpinnerGodot;
using Majo.Runtime.Script;
using MajoDialogueOption = Majo.Runtime.Dialogue.DialogueOption;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed partial class YarnDialoguePresenterAdapter : Node, DialoguePresenterBase
{
    private readonly IReadOnlyList<IDialoguePresenter> presenters;
    private readonly Action requestHurryUp;
    private readonly Action requestNextLine;
    private readonly Func<LocalizedLine, DialogueLine> lineConverter;
    private readonly Action<DialogueLine, IReadOnlyList<string>> linePresented;
    private readonly Action<IReadOnlyList<MajoDialogueOption>> optionsPresented;
    private readonly object stopGate = new();
    private TaskCompletionSource stopBoundary = NewStopBoundary();
    private bool stopRequested;
    private int activePresentations;
    private TaskCompletionSource? localeBoundary;
    private readonly LineBoundaryGate boundaries = new();
    private LineBoundaryGate.Lease? presentedBoundary;
    private CancellationToken presentedNextLineToken;

    public bool CanReviseBoundary(LineBoundaryContext context) =>
        presentedBoundary is { } boundary &&
        boundary.Generation == context.SessionGeneration &&
        boundary.Sequence == context.OccurrenceSequence &&
        !boundary.Token.IsCancellationRequested && !presentedNextLineToken.IsCancellationRequested;

    public YarnDialoguePresenterAdapter(
        IReadOnlyList<IDialoguePresenter> presenters,
        Action requestHurryUp,
        Action requestNextLine,
        Action<DialogueLine, IReadOnlyList<string>> linePresented,
        Action<IReadOnlyList<MajoDialogueOption>> optionsPresented,
        Func<LocalizedLine, DialogueLine>? lineConverter = null)
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
        this.lineConverter = lineConverter ?? (line => YarnLineConverter.Convert(line));
    }

    public List<DialoguePresenterBase> NativePresenters { get; } = new();

    public Action<bool>? PreparationChanged { get; set; }
    public Action<Exception>? BoundaryFailed { get; set; }

    public Action? OptionsStarting { get; set; }

    public Func<DialogueLine, CancellationToken, Task>? PrepareLineAsync { get; set; }

    public Func<DialogueLine, IReadOnlyList<string>, long, long, CancellationToken, Task>?
        CommitLineBoundaryAsync
    { get; set; }

    internal void ReleaseBoundaryResources() => boundaries.Dispose();

    public Task<IDisposable> AcquireRestoreAsync(CancellationToken token) => boundaries.AcquireRestoreAsync(token);

    public Task WaitForPreparationAsync(CancellationToken token) => boundaries.WaitForPreparationAsync(token);

    public bool DeferAdvance(bool hurryUp = false) => boundaries.DeferAdvance(hurryUp);

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public void BeginSession()
    {
        boundaries.BeginSession();
        lock (stopGate)
        {
            stopRequested = false;
            activePresentations = 0;
            stopBoundary = NewStopBoundary();
        }
    }

    public Task PrepareToStop()
    {
        boundaries.Stop();
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
        foreach (var presenter in NativePresenters.ToArray()) await presenter.OnDialogueStartedAsync();
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

        LineBoundaryGate.Lease? boundary = null;
        var activated = false;
        try
        {
            var converted = lineConverter(line);
            boundary = boundaries.Enter(converted.Origin != DialoguePresentationOrigin.Normal);
            PreparationChanged?.Invoke(true);
            if (PrepareLineAsync is not null) await PrepareLineAsync(converted, boundary.Token);
            boundary.Token.ThrowIfCancellationRequested();
            var substitutions = Array.AsReadOnly(line.Substitutions.ToArray());
            if (CommitLineBoundaryAsync is not null)
            {
                await CommitLineBoundaryAsync(
                    converted, substitutions, boundary.Generation, boundary.Sequence, boundary.Token);
            }
            boundary.Token.ThrowIfCancellationRequested();
            presentedBoundary = boundary;
            presentedNextLineToken = token.NextLineToken;
            linePresented(converted, substitutions);
            var context = new DialoguePresentationContext(
                requestHurryUp,
                requestNextLine,
                token.HurryUpToken,
                token.NextLineToken);
            var presentation = Task.WhenAll(presenters.Select(
                presenter => presenter.PresentLineAsync(converted, context)));
            var nativePresentation = Task.WhenAll(NativePresenters.ToArray().Select(
                async presenter => await presenter.RunLineAsync(line, token)));
            activated = true;
            PreparationChanged?.Invoke(false);
            boundary.Activate(requestNextLine, requestHurryUp);
            try
            {
                await presentation.WaitAsync(token.NextLineToken);
            }
            catch (OperationCanceledException) when (token.NextLineToken.IsCancellationRequested)
            {
                _ = ObserveDetachedAsync(new[] { presentation });
            }
            // Native presenters own dismissal animations and must drain before a replacement line.
            await nativePresentation;
        }
        catch (OperationCanceledException) when (
            boundary?.Token.IsCancellationRequested == true || token.NextLineToken.IsCancellationRequested)
        {
        }
        catch (Exception failure) when (!activated)
        {
            boundaries.Stop();
            BoundaryFailed?.Invoke(failure);
            // Yarn catches presenter exceptions and continues; keep this line blocked until Stop.
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token.NextLineToken); }
            catch (OperationCanceledException) when (token.NextLineToken.IsCancellationRequested) { }
        }
        finally
        {
            if (ReferenceEquals(presentedBoundary, boundary)) presentedBoundary = null;
            boundary?.Dispose();
            if (!activated) PreparationChanged?.Invoke(false);
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
        OptionsStarting?.Invoke();
        var converted = dialogueOptions.Select(option => new MajoDialogueOption(
            option.DialogueOptionID,
            lineConverter(option.Line),
            option.IsAvailable,
            Array.AsReadOnly(option.Line.Substitutions.ToArray()))).ToArray();
        optionsPresented(converted);
        if (presenters.Count == 0 && NativePresenters.Count == 0)
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
        pending.AddRange(NativePresenters.ToArray().Select(async presenter =>
        {
            try
            {
                var selection = await presenter.RunOptionsAsync(dialogueOptions, selectionCancellation.Token);
                return selection is null ? null : converted.FirstOrDefault(
                    option => option.Id == selection.DialogueOptionID);
            }
            catch (OperationCanceledException) when (selectionCancellation.IsCancellationRequested)
            {
                return null;
            }
        }));

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
        foreach (var presenter in NativePresenters.ToArray()) await presenter.OnDialogueCompleteAsync();
        foreach (var presenter in presenters)
        {
            await presenter.OnDialogueCompletedAsync(CancellationToken.None);
        }
    }

    public void OnNodeEnter(string nodeName)
    {
        foreach (var presenter in NativePresenters.ToArray()) presenter.OnNodeEnter(nodeName);
        foreach (var presenter in presenters)
        {
            presenter.OnNodeEntered(nodeName);
        }
    }

    public void OnNodeExit(string nodeName)
    {
        foreach (var presenter in NativePresenters.ToArray()) presenter.OnNodeExit(nodeName);
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
            localeBoundary?.TrySetResult();
            localeBoundary = null;
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
            localeBoundary?.TrySetResult();
            localeBoundary = null;
        }
    }

    public Task WaitForPresentationBoundaryAsync(CancellationToken cancellationToken)
    {
        lock (stopGate)
        {
            if (activePresentations > 0 || stopRequested)
            {
                return Task.CompletedTask;
            }

            localeBoundary ??= NewStopBoundary();
            return localeBoundary.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewStopBoundary() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}

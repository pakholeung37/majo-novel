#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Input;

namespace Majo.Runtime.History;

public sealed class HistoryManager : IHistoryManager
{
    private readonly IDialoguePlayer dialogue;
    private readonly IDialoguePresentation presentation;
    private readonly IAudioManager audio;
    private readonly IInputManager input;
    private readonly IRuntimeLogger logger;
    private readonly List<HistoryEntry> entries = new();
    private IInputHandle? toggleInput;
    private IInputHandle? modalInput;
    private DialogueLine? pendingLine;
    private long nextSequence;
    private bool isInitialized;

    public HistoryManager(
        IDialoguePlayer dialogue,
        IDialoguePresentation presentation,
        IAudioManager audio,
        IInputManager input,
        int capacity,
        IRuntimeLogger? logger = null)
    {
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.presentation = presentation ??
            throw new ArgumentNullException(nameof(presentation));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
    }

    public event Action<HistoryOpenStateChanged>? OpenStateChanged;

    public event Action<HistoryEntriesChanged>? EntriesChanged;

    public IReadOnlyList<HistoryEntry> Entries => entries.AsReadOnly();

    public int Capacity { get; }

    public bool IsOpen { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        dialogue.LinePresented += HandleLinePresented;
        dialogue.Completed += HandleDialogueEnded;
        dialogue.Stopped += HandleDialogueEnded;
        presentation.LineDisplayCompleted += HandleLineDisplayCompleted;
        EnsureToggleInput();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();
        pendingLine = null;
        Clear();
        EnsureToggleInput();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        Close();
        dialogue.LinePresented -= HandleLinePresented;
        dialogue.Completed -= HandleDialogueEnded;
        dialogue.Stopped -= HandleDialogueEnded;
        presentation.LineDisplayCompleted -= HandleLineDisplayCompleted;
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
            toggleInput.Dispose();
            toggleInput = null;
        }

        pendingLine = null;
        Clear();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void Open()
    {
        EnsureInitialized();
        if (IsOpen)
        {
            return;
        }

        modalInput = input.AcquireScope(
            this,
            new InputScopeOptions(
                [MajoInputActions.History, MajoInputActions.Cancel],
                isModal: true,
                name: "history-modal"));
        modalInput.Activated += HandleModalInput;
        IsOpen = true;
        logger.Info("history", "history.opened", "Dialogue history opened.");
        OpenStateChanged?.Invoke(new HistoryOpenStateChanged(true));
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        if (modalInput is not null)
        {
            modalInput.Activated -= HandleModalInput;
            modalInput.Dispose();
            modalInput = null;
        }

        IsOpen = false;
        logger.Info("history", "history.closed", "Dialogue history closed.");
        OpenStateChanged?.Invoke(new HistoryOpenStateChanged(false));
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Clear()
    {
        if (entries.Count == 0 && nextSequence == 0)
        {
            return;
        }

        entries.Clear();
        nextSequence = 0;
        PublishEntries();
    }

    public async Task<bool> ReplayVoiceAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        var entry = entries.SingleOrDefault(item => item.Sequence == sequence);
        if (entry?.VoiceId is null)
        {
            return false;
        }

        try
        {
            var result = await audio.PlayVoiceAsync(
                entry.VoiceId,
                cancellationToken: cancellationToken);
            if (result.IsSkipped)
            {
                return false;
            }
            logger.Debug(
                "history",
                "history.voice_replayed",
                $"Replayed history voice for line '{entry.LineId}'.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "history",
                "history.voice_replay_failed",
                $"History voice replay failed for line '{entry.LineId}': {exception.Message}",
                exception);
            throw;
        }
    }

    private void HandleLinePresented(DialogueLine line) => pendingLine = line;

    private void HandleLineDisplayCompleted()
    {
        if (pendingLine is not { } line)
        {
            return;
        }

        entries.Add(new HistoryEntry(
            ++nextSequence,
            line.Id,
            line.Speaker,
            line.Text.Text,
            DialogueVoiceResolver.ResolveExistingVoiceId(line, audio)));
        if (entries.Count > Capacity)
        {
            entries.RemoveRange(0, entries.Count - Capacity);
        }

        pendingLine = null;
        PublishEntries();
    }

    private void HandleDialogueEnded(DialogueCompletion _) => pendingLine = null;

    private void HandleToggleInput(InputActivation _) => Toggle();

    private void EnsureToggleInput()
    {
        if (toggleInput is { IsDisposed: false })
        {
            return;
        }

        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
        }

        toggleInput = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.History], name: "history-toggle"));
        toggleInput.Activated += HandleToggleInput;
    }

    private void HandleModalInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.History ||
            activation.Action == MajoInputActions.Cancel)
        {
            Close();
        }
    }

    private void PublishEntries() =>
        EntriesChanged?.Invoke(new HistoryEntriesChanged(Entries));

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("HistoryManager is not initialized.");
        }
    }
}

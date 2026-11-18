#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Script;

namespace Majo.Runtime.Save;

public sealed class SaveProductManager : ISaveProductManager
{
    private readonly object sync = new();
    private readonly ISaveSlotManager slots;
    private readonly IDialoguePlayer dialogue;
    private readonly IDialoguePresentation presentation;
    private readonly IScriptPlayback script;
    private readonly IInputManager input;
    private readonly HashSet<string> nodeConditions;
    private readonly HashSet<string> lineConditions;
    private readonly HashSet<string> metadataConditions;
    private readonly IRuntimeLogger logger;
    private IInputHandle? quickSaveInput;
    private DialogueLine? currentLine;
    private AutoSaveCandidate? quickCandidate;
    private AutoSaveCandidate? candidate;
    private string? projectQuickCandidateForNextBoundary;
    private string? projectCandidateForNextBoundary;
    private long boundaryId;
    private long? attemptedQuickBoundary;
    private long? attemptedBoundary;
    private bool isInitialized;

    public SaveProductManager(
        ISaveSlotManager slots,
        IDialoguePlayer dialogue,
        IDialoguePresentation presentation,
        IScriptPlayback script,
        IInputManager input,
        IEnumerable<string>? autoSaveNodes = null,
        IEnumerable<string>? autoSaveLines = null,
        IEnumerable<string>? autoSaveMetadata = null,
        IRuntimeLogger? logger = null)
    {
        this.slots = slots ?? throw new ArgumentNullException(nameof(slots));
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        nodeConditions = NormalizeConditions(autoSaveNodes);
        lineConditions = NormalizeConditions(autoSaveLines);
        metadataConditions = NormalizeConditions(autoSaveMetadata);
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<AutoSaveCandidate>? AutoSaveCandidateAccepted;
    public event Action<SaveSlotMetadata>? AutoSaveCommitted;
    public event Action<Exception>? AutoSaveFailed;

    public AutoSaveCandidate? PendingAutoSave
    {
        get { lock (sync) { return candidate; } }
    }

    public long? LastCommittedAutoSaveBoundary { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }
        dialogue.LinePresented += HandleLinePresented;
        presentation.LineDisplayCompleted += HandleLineDisplayCompleted;
        presentation.LineDismissed += HandleLineDismissed;
        EnsureQuickSaveInput();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            currentLine = null;
            quickCandidate = null;
            candidate = null;
            projectQuickCandidateForNextBoundary = null;
            projectCandidateForNextBoundary = null;
            attemptedQuickBoundary = null;
            attemptedBoundary = null;
            boundaryId = 0;
        }
        LastCommittedAutoSaveBoundary = null;
        EnsureQuickSaveInput();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }
        dialogue.LinePresented -= HandleLinePresented;
        presentation.LineDisplayCompleted -= HandleLineDisplayCompleted;
        presentation.LineDismissed -= HandleLineDismissed;
        if (quickSaveInput is not null)
        {
            quickSaveInput.Activated -= HandleQuickSaveInput;
            quickSaveInput.Dispose();
            quickSaveInput = null;
        }
        lock (sync)
        {
            currentLine = null;
            quickCandidate = null;
            candidate = null;
            projectQuickCandidateForNextBoundary = null;
            projectCandidateForNextBoundary = null;
            attemptedQuickBoundary = null;
            attemptedBoundary = null;
        }
        isInitialized = false;
        return Task.CompletedTask;
    }

    public Task<SaveSlotMetadata> QuickSaveAsync(
        CancellationToken cancellationToken = default) =>
        slots.SaveRotatingAsync(SaveSlotKind.Quick, cancellationToken: cancellationToken);

    public void RequestQuickSaveCandidate(string source = "project-api")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        EnsureInitialized();
        lock (sync)
        {
            if (currentLine is null)
            {
                projectQuickCandidateForNextBoundary = source.Trim();
                return;
            }
            quickCandidate = CreateCandidate(source.Trim());
        }
    }

    public void RequestAutoSaveCandidate(string source = "project-api")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        EnsureInitialized();
        AutoSaveCandidate? accepted = null;
        lock (sync)
        {
            if (currentLine is null)
            {
                projectCandidateForNextBoundary = source.Trim();
                return;
            }
            accepted = candidate = CreateCandidate(source.Trim());
        }
        Publish(
            AutoSaveCandidateAccepted,
            accepted!,
            "save.auto_candidate_observer_failed");
    }

    private void HandleLinePresented(DialogueLine line)
    {
        if (line.Origin == DialoguePresentationOrigin.HotReloadReplay)
        {
            lock (sync)
            {
                currentLine = line;
                quickCandidate = null;
                candidate = null;
            }
            return;
        }
        AutoSaveCandidate? accepted = null;
        lock (sync)
        {
            currentLine = line;
            boundaryId++;
            attemptedQuickBoundary = null;
            attemptedBoundary = null;
            quickCandidate = projectQuickCandidateForNextBoundary is { } quickSource
                ? CreateCandidate(quickSource)
                : null;
            projectQuickCandidateForNextBoundary = null;
            var source = MatchConfiguredSource(line);
            if (projectCandidateForNextBoundary is { } projectSource)
            {
                source = projectSource;
                projectCandidateForNextBoundary = null;
            }
            candidate = source is null ? null : CreateCandidate(source);
            accepted = candidate;
        }
        if (accepted is not null)
        {
            Publish(
                AutoSaveCandidateAccepted,
                accepted,
                "save.auto_candidate_observer_failed");
        }
    }

    private void HandleLineDismissed()
    {
        lock (sync)
        {
            currentLine = null;
        }
    }

    private void HandleLineDisplayCompleted()
    {
        AutoSaveCandidate? pendingQuick;
        AutoSaveCandidate? pending;
        lock (sync)
        {
            pendingQuick = quickCandidate;
            pending = candidate;
            if (script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
                script.CurrentSpot?.LineId is not { } currentLineId)
            {
                return;
            }

            if (pendingQuick is not null &&
                pendingQuick.LineId == currentLineId &&
                attemptedQuickBoundary != pendingQuick.BoundaryId)
            {
                attemptedQuickBoundary = pendingQuick.BoundaryId;
                quickCandidate = null;
            }
            else
            {
                pendingQuick = null;
            }

            if (pending is not null &&
                pending.LineId == currentLineId &&
                attemptedBoundary != pending.BoundaryId)
            {
                attemptedBoundary = pending.BoundaryId;
                candidate = null;
            }
            else
            {
                pending = null;
            }
        }
        if (pendingQuick is not null)
        {
            _ = CommitQuickSaveAsync(pendingQuick);
        }
        if (pending is not null)
        {
            _ = CommitAutoSaveAsync(pending);
        }
    }

    private async Task CommitQuickSaveAsync(AutoSaveCandidate pending)
    {
        try
        {
            await slots.SaveRotatingAsync(
                SaveSlotKind.Quick,
                new SaveSlotSaveRequest(
                    DeveloperMetadata: new Dictionary<string, string>
                    {
                        ["saveSource"] = pending.Source,
                        ["saveBoundary"] = pending.BoundaryId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    }));
        }
        catch (Exception exception)
        {
            logger.Warning(
                "save",
                "save.command_failed",
                $"Script save at boundary '{pending.BoundaryId}' failed: {exception.Message}",
                exception);
        }
    }

    private async Task CommitAutoSaveAsync(AutoSaveCandidate pending)
    {
        try
        {
            var metadata = await slots.SaveRotatingAsync(
                SaveSlotKind.Auto,
                new SaveSlotSaveRequest(
                    DeveloperMetadata: new Dictionary<string, string>
                    {
                        ["autoSaveSource"] = pending.Source,
                        ["autoSaveBoundary"] = pending.BoundaryId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    }));
            LastCommittedAutoSaveBoundary = pending.BoundaryId;
            Publish(
                AutoSaveCommitted,
                metadata,
                "save.auto_committed_observer_failed");
        }
        catch (Exception exception)
        {
            logger.Warning(
                "save",
                "save.auto_failed",
                $"Auto save at boundary '{pending.BoundaryId}' failed: {exception.Message}",
                exception);
            Publish(
                AutoSaveFailed,
                exception,
                "save.auto_failed_observer_failed");
        }
    }

    private void HandleQuickSaveInput(InputActivation activation)
    {
        _ = RunQuickSaveFromInputAsync();
    }

    private async Task RunQuickSaveFromInputAsync()
    {
        try
        {
            await QuickSaveAsync();
        }
        catch (Exception exception)
        {
            logger.Warning(
                "save",
                "save.quick_failed",
                $"Quick save failed: {exception.Message}",
                exception);
        }
    }

    private void EnsureQuickSaveInput()
    {
        if (quickSaveInput is { IsDisposed: false })
        {
            return;
        }
        if (quickSaveInput is not null)
        {
            quickSaveInput.Activated -= HandleQuickSaveInput;
        }
        quickSaveInput = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.QuickSave], name: "quick-save"));
        quickSaveInput.Activated += HandleQuickSaveInput;
    }

    private string? MatchConfiguredSource(DialogueLine line)
    {
        if (dialogue.CurrentNode is { } node && nodeConditions.Contains(node))
        {
            return $"node:{node}";
        }
        if (lineConditions.Contains(line.Id))
        {
            return $"line:{line.Id}";
        }
        var metadata = line.Metadata.FirstOrDefault(metadataConditions.Contains);
        return metadata is null ? null : $"metadata:{metadata}";
    }

    private AutoSaveCandidate CreateCandidate(string source) => new(
        boundaryId,
        dialogue.CurrentProjectId,
        dialogue.CurrentNode,
        currentLine?.Id,
        source);

    private static HashSet<string> NormalizeConditions(IEnumerable<string>? values) =>
        new((values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()), StringComparer.Ordinal);

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new SaveSlotException("Save product manager is not initialized.");
        }
    }

    private void Publish<T>(Action<T>? observers, T value, string eventId)
    {
        if (observers is null)
        {
            return;
        }
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "save",
                    eventId,
                    $"Save product observer failed: {exception.Message}",
                    exception);
            }
        }
    }
}

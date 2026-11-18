#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue;

public sealed class DialoguePlayer : IDialoguePlayer
{
    private readonly object sync = new();
    private readonly IDialogueBackend backend;
    private readonly string defaultProjectId;
    private readonly string defaultStartNode;
    private readonly IRuntimeLogger logger;
    private TaskCompletionSource<DialogueCompletion> completion = CompletedSource();
    private CancellationTokenSource? startCancellation;
    private Task? startTask;
    private Task? stopTask;
    private long sessionVersion;
    private string? currentStartNode;
    private string? currentDialogueSessionId;
    private bool isInitialized;

    public DialoguePlayer(
        IDialogueBackend backend,
        string defaultProjectId,
        string defaultStartNode,
        IRuntimeLogger? logger = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.defaultProjectId = RequireId(defaultProjectId, nameof(defaultProjectId));
        this.defaultStartNode = RequireId(defaultStartNode, nameof(defaultStartNode));
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<DialogueStateChanged>? StateChanged;

    public event Action<DialogueStartRequest>? Started;

    public event Action<DialogueLine>? LinePresented;

    public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

    public event Action<DialogueCompletion>? Completed;

    public event Action<DialogueCompletion>? Stopped;

    public event Action<Exception>? Failed;

    public DialogueSessionState State { get; private set; } = DialogueSessionState.Idle;

    public string? CurrentProjectId { get; private set; }

    public string? CurrentNode { get; private set; }

    public string? CurrentLineId { get; private set; }

    public string? CurrentDialogueSessionId
    {
        get { lock (sync) { return currentDialogueSessionId; } }
    }

    public bool IsPresentingOptions { get; private set; }

    public IReadOnlyList<DialogueOption> CurrentOptions { get; private set; } =
        Array.Empty<DialogueOption>();

    public Task<DialogueCompletion> Completion
    {
        get
        {
            lock (sync)
            {
                return completion.Task;
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (isInitialized)
            {
                return Task.CompletedTask;
            }

            backend.NodeEntered += HandleNodeEntered;
            backend.LinePresented += HandleLinePresented;
            backend.OptionsPresented += HandleOptionsPresented;
            backend.Failed += HandleBackendFailed;
            isInitialized = true;
        }

        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        DialogueStateChanged? stateChange = null;
        lock (sync)
        {
            CurrentProjectId = null;
            CurrentNode = null;
            CurrentLineId = null;
            currentStartNode = null;
            currentDialogueSessionId = null;
            IsPresentingOptions = false;
            CurrentOptions = Array.Empty<DialogueOption>();
            if (State == DialogueSessionState.Faulted)
            {
                stateChange = SetStateLocked(DialogueSessionState.Idle);
            }
        }

        NotifyStateChanged(stateChange);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        lock (sync)
        {
            if (!isInitialized)
            {
                return;
            }

            backend.NodeEntered -= HandleNodeEntered;
            backend.LinePresented -= HandleLinePresented;
            backend.OptionsPresented -= HandleOptionsPresented;
            backend.Failed -= HandleBackendFailed;
            isInitialized = false;
        }
    }

    public Task StartAsync(
        DialogueStartRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var projectId = RequireId(
            request?.ProjectId ?? defaultProjectId,
            nameof(DialogueStartRequest.ProjectId));
        var startNode = RequireId(
            request?.StartNode ?? defaultStartNode,
            nameof(DialogueStartRequest.StartNode));

        var publication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task starting;
        DialogueStateChanged? stateChange;
        lock (sync)
        {
            if (!isInitialized)
            {
                throw new DialogueStateException(State, "start an uninitialized");
            }

            if (State is not DialogueSessionState.Idle and not DialogueSessionState.Faulted)
            {
                throw new DialogueStateException(State, "start");
            }

            sessionVersion++;
            var version = sessionVersion;
            startCancellation?.Dispose();
            startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            completion = new TaskCompletionSource<DialogueCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = null;
            CurrentProjectId = projectId;
            CurrentNode = null;
            CurrentLineId = null;
            currentStartNode = startNode;
            currentDialogueSessionId = Guid.NewGuid().ToString("N");
            IsPresentingOptions = false;
            CurrentOptions = Array.Empty<DialogueOption>();
            stateChange = SetStateLocked(DialogueSessionState.Loading);
            startTask = StartAfterPublicationAsync(
                publication.Task,
                version,
                projectId,
                startNode,
                startCancellation.Token);
            starting = startTask;
        }

        logger.Info(
            "dialogue",
            "dialogue.session_start_requested",
            $"Dialogue start requested for project '{projectId}', node '{startNode}'.",
            new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId),
            new Dictionary<string, object?>
            {
                ["projectId"] = projectId,
                ["node"] = startNode,
            });

        NotifyStateChanged(stateChange);
        publication.TrySetResult();
        return starting;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        var publication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task stopping;
        DialogueStateChanged? stateChange;
        lock (sync)
        {
            if (State == DialogueSessionState.Idle)
            {
                return Task.CompletedTask;
            }

            if (State == DialogueSessionState.Stopping)
            {
                return stopTask ?? Task.CompletedTask;
            }

            var version = sessionVersion;
            stateChange = SetStateLocked(DialogueSessionState.Stopping);
            startCancellation?.Cancel();
            stopTask = StopAfterPublicationAsync(
                publication.Task,
                version,
                cancellationToken);
            stopping = stopTask;
        }

        logger.Info(
            "dialogue",
            "dialogue.session_stop_requested",
            $"Dialogue stop requested for project '{CurrentProjectId ?? defaultProjectId}'.",
            new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId));

        NotifyStateChanged(stateChange);
        publication.TrySetResult();
        return stopping;
    }

    public void RequestHurryUp() => backend.RequestHurryUp();

    public void RequestNextLine() => backend.RequestNextLine();

    private async Task StartAfterPublicationAsync(
        Task publication,
        long version,
        string projectId,
        string startNode,
        CancellationToken cancellationToken)
    {
        await publication;
        await StartCoreAsync(version, projectId, startNode, cancellationToken);
    }

    private async Task StopAfterPublicationAsync(
        Task publication,
        long version,
        CancellationToken cancellationToken)
    {
        await publication;
        await StopCoreAsync(version, cancellationToken);
    }

    private async Task StartCoreAsync(
        long version,
        string projectId,
        string startNode,
        CancellationToken cancellationToken)
    {
        try
        {
            await backend.StartAsync(projectId, startNode, cancellationToken);
            DialogueStateChanged? stateChange;
            lock (sync)
            {
                if (version != sessionVersion || State == DialogueSessionState.Stopping)
                {
                    return;
                }

                stateChange = SetStateLocked(DialogueSessionState.Running);
            }

            logger.Info(
                "dialogue",
                "dialogue.session_started",
                $"Dialogue running for project '{projectId}', node '{startNode}'.",
                new RuntimeLogCorrelation(
                    string.Empty,
                    dialogueSessionId: CurrentDialogueSessionId));

            NotifyStateChanged(stateChange);
            Started?.Invoke(new DialogueStartRequest(projectId, startNode));
            _ = ObserveCompletionAsync(version);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested &&
            IsStopping(version))
        {
        }
        catch (Exception exception)
        {
            Fault(version, exception);
            throw;
        }
    }

    private async Task StopCoreAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await backend.StopAsync(cancellationToken);
            var pendingStart = startTask;
            if (pendingStart is not null)
            {
                try
                {
                    await pendingStart;
                }
                catch (Exception)
                {
                    // StartCoreAsync already publishes the failure to the start caller
                    // and session completion. Stopping only needs to drain that work;
                    // a previous startup failure must not prevent cleanup or recovery.
                }
            }

            DialogueCompletion stopped;
            DialogueStateChanged? stateChange;
            lock (sync)
            {
                if (version != sessionVersion)
                {
                    return;
                }

                stopped = new DialogueCompletion(
                    DialogueCompletionKind.Stopped,
                    CurrentProjectId ?? defaultProjectId,
                    currentStartNode ?? defaultStartNode);
                completion.TrySetResult(stopped);
                IsPresentingOptions = false;
                CurrentOptions = Array.Empty<DialogueOption>();
                stateChange = SetStateLocked(DialogueSessionState.Idle);
            }

            logger.Info(
                "dialogue",
                "dialogue.session_stopped",
                $"Dialogue stopped for project '{stopped.ProjectId}'.",
                new RuntimeLogCorrelation(
                    string.Empty,
                    dialogueSessionId: CurrentDialogueSessionId));

            NotifyStateChanged(stateChange);
            Stopped?.Invoke(stopped);
        }
        catch (Exception exception)
        {
            Fault(version, exception);
            throw;
        }
    }

    private async Task ObserveCompletionAsync(long version)
    {
        try
        {
            var result = await backend.Completion;
            DialogueStateChanged? stateChange;
            TaskCompletionSource<DialogueCompletion> sessionCompletion;
            lock (sync)
            {
                if (version != sessionVersion || State == DialogueSessionState.Stopping)
                {
                    return;
                }

                sessionCompletion = completion;
                IsPresentingOptions = false;
                CurrentOptions = Array.Empty<DialogueOption>();
                stateChange = SetStateLocked(DialogueSessionState.Idle);
            }

            NotifyStateChanged(stateChange);
            if (result.Kind == DialogueCompletionKind.Stopped)
            {
                logger.Info(
                    "dialogue",
                    "dialogue.session_stopped",
                    $"Dialogue stopped for project '{result.ProjectId}'.",
                    new RuntimeLogCorrelation(
                        string.Empty,
                        dialogueSessionId: CurrentDialogueSessionId));
                Stopped?.Invoke(result);
            }
            else
            {
                logger.Info(
                    "dialogue",
                    "dialogue.session_completed",
                    $"Dialogue completed for project '{result.ProjectId}', node '{result.StartNode}'.",
                    new RuntimeLogCorrelation(
                        string.Empty,
                        dialogueSessionId: CurrentDialogueSessionId));
                Completed?.Invoke(result);
            }

            sessionCompletion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            Fault(version, exception);
        }
    }

    private void HandleNodeEntered(string nodeName)
    {
        lock (sync)
        {
            CurrentNode = nodeName;
        }

        logger.Debug(
            "dialogue",
            "dialogue.node_entered",
            $"Entered dialogue node '{nodeName}'.",
            new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId));
    }

    private void HandleLinePresented(DialogueLine line)
    {
        lock (sync)
        {
            CurrentLineId = line.Id;
            IsPresentingOptions = false;
            CurrentOptions = Array.Empty<DialogueOption>();
        }

        logger.Debug(
            "dialogue",
            "dialogue.line_presented",
            $"Presented dialogue line '{line.Id}' (Speaker: '{line.Speaker ?? "<narrator>"}').",
            new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId),
            new Dictionary<string, object?> { ["lineId"] = line.Id });

        LinePresented?.Invoke(line);
    }

    private void HandleOptionsPresented(IReadOnlyList<DialogueOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<DialogueOption> snapshot = options.Count == 0
            ? Array.Empty<DialogueOption>()
            : new List<DialogueOption>(options).AsReadOnly();
        lock (sync)
        {
            IsPresentingOptions = true;
            CurrentOptions = snapshot;
        }

        logger.Debug(
            "dialogue",
            "dialogue.options_presented",
            $"Presented {snapshot.Count} dialogue options.",
            new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId));

        OptionsPresented?.Invoke(snapshot);
    }

    private void HandleBackendFailed(Exception exception)
    {
        long version;
        lock (sync)
        {
            if (State is DialogueSessionState.Idle or DialogueSessionState.Faulted)
            {
                return;
            }

            version = sessionVersion;
        }

        Fault(version, exception);
    }

    private bool IsStopping(long version)
    {
        lock (sync)
        {
            return version == sessionVersion && State == DialogueSessionState.Stopping;
        }
    }

    private void Fault(long version, Exception exception)
    {
        var notify = false;
        DialogueStateChanged? stateChange = null;
        lock (sync)
        {
            if (version != sessionVersion || State == DialogueSessionState.Faulted)
            {
                return;
            }

            completion.TrySetException(exception);
            _ = completion.Task.Exception;
            IsPresentingOptions = false;
            CurrentOptions = Array.Empty<DialogueOption>();
            stateChange = SetStateLocked(DialogueSessionState.Faulted);
            notify = true;
        }

        logger.Error(
            "dialogue",
            "dialogue.session_faulted",
            $"Dialogue faulted: {exception.Message}",
            correlation: new RuntimeLogCorrelation(
                string.Empty,
                dialogueSessionId: CurrentDialogueSessionId));

        if (notify)
        {
            NotifyStateChanged(stateChange);
            Failed?.Invoke(exception);
        }
    }

    private DialogueStateChanged? SetStateLocked(DialogueSessionState next)
    {
        if (State == next)
        {
            return null;
        }

        var previous = State;
        State = next;
        return new DialogueStateChanged(previous, next);
    }

    private void NotifyStateChanged(DialogueStateChanged? change)
    {
        if (change is not null)
        {
            StateChanged?.Invoke(change);
        }
    }

    private static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static TaskCompletionSource<DialogueCompletion> CompletedSource()
    {
        var source = new TaskCompletionSource<DialogueCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(new DialogueCompletion(
            DialogueCompletionKind.Completed,
            string.Empty,
            string.Empty));
        return source;
    }
}

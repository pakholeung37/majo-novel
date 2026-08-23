#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue;

public sealed class DialoguePlayer : IDialoguePlayer
{
    private readonly object sync = new();
    private readonly IDialogueBackend backend;
    private readonly string defaultProjectId;
    private readonly string defaultStartNode;
    private TaskCompletionSource<DialogueCompletion> completion = CompletedSource();
    private CancellationTokenSource? startCancellation;
    private Task? startTask;
    private Task? stopTask;
    private long sessionVersion;
    private string? currentStartNode;
    private bool isInitialized;

    public DialoguePlayer(
        IDialogueBackend backend,
        string defaultProjectId,
        string defaultStartNode)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.defaultProjectId = RequireId(defaultProjectId, nameof(defaultProjectId));
        this.defaultStartNode = RequireId(defaultStartNode, nameof(defaultStartNode));
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
                catch (OperationCanceledException)
                {
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
            lock (sync)
            {
                if (version != sessionVersion || State == DialogueSessionState.Stopping)
                {
                    return;
                }

                completion.TrySetResult(result);
                IsPresentingOptions = false;
                CurrentOptions = Array.Empty<DialogueOption>();
                stateChange = SetStateLocked(DialogueSessionState.Idle);
            }

            NotifyStateChanged(stateChange);
            if (result.Kind == DialogueCompletionKind.Stopped)
            {
                Stopped?.Invoke(result);
            }
            else
            {
                Completed?.Invoke(result);
            }
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
    }

    private void HandleLinePresented(DialogueLine line)
    {
        lock (sync)
        {
            CurrentLineId = line.Id;
            IsPresentingOptions = false;
            CurrentOptions = Array.Empty<DialogueOption>();
        }

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

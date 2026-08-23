#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnDialogueBackend : IYarnDialogueBackend
{
    private readonly Node owner;
    private readonly IResourceLoader<YarnProject> projects;
    private readonly IReadOnlyList<IDialoguePresenter> presenters;
    private readonly YarnDialogueVariableStore variableStore = new();
    private readonly object sync = new();
    private DialogueRunner? runner;
    private YarnDialoguePresenterAdapter? presenterAdapter;
    private IYarnCommandAdapter? commandAdapter;
    private Callable nodeStarted;
    private Callable nodeCompleted;
    private Callable dialogueCompleted;
    private TaskCompletionSource<DialogueCompletion> completion = CompletedSource();
    private string currentProjectId = string.Empty;
    private string currentStartNode = string.Empty;
    private Exception? pendingFailure;
    private bool isStopping;
    private bool isInitialized;

    public YarnDialogueBackend(
        Node owner,
        IResourceManager resources,
        string scriptsPath,
        IReadOnlyList<IDialoguePresenter> presenters)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(resources);
        projects = resources.CreateLoader<YarnProject>(scriptsPath);
        this.presenters = presenters ?? throw new ArgumentNullException(nameof(presenters));
    }

    public event Action<string>? NodeEntered;

    public event Action<string>? NodeExited;

    public event Action<DialogueLine>? LinePresented;

    public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

    public event Action<Exception>? Failed;

    public DialogueRunner Runner => runner ?? throw new DialogueException(
        "Yarn dialogue backend has not been initialized.");

    public IDialogueVariableStore VariableStore => variableStore;

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
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        runner = new DialogueRunner
        {
            Name = "MajoDialogueRunner",
            autoStart = false,
        };
        presenterAdapter = new YarnDialoguePresenterAdapter(
            presenters,
            runner,
            line => LinePresented?.Invoke(line),
            options => OptionsPresented?.Invoke(options))
        {
            Name = "MajoPresenterAdapter",
        };
        variableStore.Name = "MajoDialogueVariables";
        runner.AddChild(variableStore);
        runner.VariableStorage = variableStore;
        runner.AddChild(presenterAdapter);
        runner.dialoguePresenters = new Godot.Collections.Array<Node?> { presenterAdapter };
        owner.AddChild(runner);

        nodeStarted = Callable.From<string>(HandleNodeStarted);
        nodeCompleted = Callable.From<string>(HandleNodeCompleted);
        dialogueCompleted = Callable.From(HandleDialogueCompleted);
        runner.Connect(DialogueRunner.SignalName.onNodeStart, nodeStarted);
        runner.Connect(DialogueRunner.SignalName.onNodeComplete, nodeCompleted);
        runner.Connect(DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        pendingFailure = null;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        await StopAsync(cancellationToken);
        if (commandAdapter is not null)
        {
            commandAdapter.CommandFailed -= HandleCommandFailed;
            commandAdapter = null;
        }

        DisconnectSignals();
        if (GodotObject.IsInstanceValid(runner))
        {
            runner!.QueueFree();
        }

        runner = null;
        presenterAdapter = null;
        isInitialized = false;
    }

    public async Task StartAsync(
        string projectId,
        string startNode,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (Runner.IsDialogueRunning)
        {
            try
            {
                await Completion.WaitAsync(cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // A failed session is fully quiescent once Completion settles;
                // its failure remains owned by the previous DialoguePlayer session.
            }
        }

        YarnProject project;
        try
        {
            project = await projects.LoadAsync(
                projectId,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DialogueProjectException(
                projectId,
                "the resource could not be loaded.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (project.Program is null)
        {
            throw new DialogueProjectException(
                projectId,
                "the Yarn resource has no compiled program.");
        }

        lock (sync)
        {
            currentProjectId = projectId;
            currentStartNode = startNode;
            pendingFailure = null;
            isStopping = false;
            completion = new TaskCompletionSource<DialogueCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        variableStore.Program = project.Program;
        Runner.SetProject(project);
        presenterAdapter!.BeginSession();
        await Runner.StartDialogue(startNode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Runner.IsDialogueRunning && !Completion.IsCompleted)
        {
            throw new DialogueProjectException(
                projectId,
                $"node '{startNode}' did not start.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return;
        }

        lock (sync)
        {
            isStopping = true;
        }

        if (Runner.IsDialogueRunning)
        {
            var waitForBoundary = commandAdapter?.HasActiveCommands == true;
            var stopBoundary = presenterAdapter!.PrepareToStop();
            commandAdapter?.CancelDialogue();
            if (commandAdapter is not null)
            {
                await commandAdapter.DrainDialogueOperationsAsync(cancellationToken);
            }

            if (Runner.IsDialogueRunning && waitForBoundary)
            {
                await stopBoundary.WaitAsync(cancellationToken);
                // The boundary is raised from the line/options callback. Keep the
                // VM active through two frame turns so the command continuation
                // that entered that callback fully retires before Stop tears down
                // Yarn's cancellation source.
                await YarnTask.NextFrame();
                await YarnTask.NextFrame();
            }

            if (Runner.IsDialogueRunning)
            {
                await Runner.Stop();
            }

            if (waitForBoundary)
            {
                // Yarn command dispatch performs its final Continue on a later
                // frame. Keep the adapter in stop-suppression mode until those
                // continuations have retired, before a new session can begin.
                await YarnTask.NextFrame();
                await YarnTask.NextFrame();
            }
        }
        else if (commandAdapter is not null)
        {
            await commandAdapter.DrainDialogueOperationsAsync(cancellationToken);
        }

        CompleteSession(DialogueCompletionKind.Stopped);
    }

    public void RequestHurryUp()
    {
        if (isInitialized)
        {
            Runner.RequestHurryUpLine();
        }
    }

    public void RequestNextLine()
    {
        if (isInitialized)
        {
            Runner.RequestNextLine();
        }
    }

    public void ObserveCommandAdapter(IYarnCommandAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (commandAdapter is not null)
        {
            throw new DialogueException("A Yarn command adapter is already observed.");
        }

        commandAdapter = adapter;
        adapter.CommandFailed += HandleCommandFailed;
    }

    private void HandleNodeStarted(string nodeName) => NodeEntered?.Invoke(nodeName);

    private void HandleNodeCompleted(string nodeName) => NodeExited?.Invoke(nodeName);

    private void HandleDialogueCompleted()
    {
        Exception? failure;
        bool stopping;
        lock (sync)
        {
            failure = pendingFailure;
            stopping = isStopping;
        }

        if (failure is not null)
        {
            completion.TrySetException(failure);
            _ = completion.Task.Exception;
            return;
        }

        CompleteSession(stopping
            ? DialogueCompletionKind.Stopped
            : DialogueCompletionKind.Completed);
    }

    private void HandleCommandFailed(RuntimeCommandException failure)
    {
        lock (sync)
        {
            pendingFailure = failure;
        }

        Failed?.Invoke(failure);
    }

    private void CompleteSession(DialogueCompletionKind kind)
    {
        lock (sync)
        {
            completion.TrySetResult(new DialogueCompletion(
                kind,
                currentProjectId,
                currentStartNode));
        }
    }

    private void DisconnectSignals()
    {
        if (!GodotObject.IsInstanceValid(runner))
        {
            return;
        }

        if (Runner.IsConnected(DialogueRunner.SignalName.onNodeStart, nodeStarted))
        {
            Runner.Disconnect(DialogueRunner.SignalName.onNodeStart, nodeStarted);
        }

        if (Runner.IsConnected(DialogueRunner.SignalName.onNodeComplete, nodeCompleted))
        {
            Runner.Disconnect(DialogueRunner.SignalName.onNodeComplete, nodeCompleted);
        }

        if (Runner.IsConnected(DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted))
        {
            Runner.Disconnect(DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new DialogueException("Yarn dialogue backend has not been initialized.");
        }
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

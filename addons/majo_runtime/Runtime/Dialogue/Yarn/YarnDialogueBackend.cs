#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn.Bridges;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Dialogue.Yarn.Preloading;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;
using Majo.Runtime.Script;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnDialogueBackend : IYarnDialogueBackend, ILineBoundarySource,
    IScriptCheckpointReplayBackend,
    IScriptCatalogBackend,
    IScriptCheckpointBackend,
    IDisposable
{
    private readonly YarnWindowPreloader? preloader;
    private CancellationTokenSource nodeEntryCancellation = new();
    private long nodeActivation;
    private long enteredActivation = -1;
    private int nodeCallDepth;
    private int nodeCompletions;
    private NodeEntryReason nodeEntryReason;

    public Func<NodeEntryRequest, CancellationToken, Task>? BeforeNodeEntryAsync { get; set; }

    private readonly Node owner;
    private readonly IYarnProjectVersionStore projectVersions;
    private readonly IDirectionScopeResolver? directionScopes;
    private readonly IReadOnlyList<IDialoguePresenter> presenters;
    private readonly ILocaleManager? localeManager;
    private readonly IRuntimeLogger logger;
    private readonly YarnDialogueVariableStore variableStore = new();
    private DialogueRunner? runner;
    private YarnDialogueRunnerBridge? runnerBridge;
    private bool lineProcessorRegistered;
    private YarnDialoguePresenterAdapter? presenterAdapter;
    private IYarnCommandAdapter? commandAdapter;
    private Callable nodeStarted;
    private Callable nodeCompleted;
    private Callable dialogueCompleted;
    private Callable dialogueStarted;
    private string currentNodeName = string.Empty;
    private string currentLineId = string.Empty;
    private IReadOnlyList<string> currentSubstitutions = Array.Empty<string>();

    private readonly YarnPlaybackSession session = new();
    private readonly YarnCheckpointController checkpoints;
    private readonly YarnLocalizedDialogue localized;
    public DialogueLine? CurrentLine => localized.CurrentLine;
    private bool isInitialized;
    private Func<CancellationToken, Task>? presentationQuiescenceWaiter;

    public YarnDialogueBackend(
        Node owner,
        IResourceManager resources,
        string scriptsPath,
        IReadOnlyList<IDialoguePresenter> presenters,
        ILocaleManager? localeManager = null,
        DialogueSpeakerNameResolver? speakerNameResolver = null,
        IRuntimeLogger? logger = null,
        bool requireExactProgramFingerprint = false,
        IYarnProjectVersionStore? projectVersions = null,
        IDirectionScopeResolver? directionScopes = null,
        YarnWindowPreloader? preloader = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(resources);
        this.projectVersions = projectVersions ??
            new YarnProjectVersionStore(resources, scriptsPath);
        this.directionScopes = directionScopes;
        this.preloader = preloader;
        this.presenters = presenters ?? throw new ArgumentNullException(nameof(presenters));
        this.localeManager = localeManager;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        checkpoints = new YarnCheckpointController(this.projectVersions, requireExactProgramFingerprint, this.logger);
        localized = new YarnLocalizedDialogue(localeManager, speakerNameResolver);
    }

    public event Action<string>? NodeEntered;

    public event Action<string>? NodeExited;

    public event Action<DialogueLine>? LinePresented;

    public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

    public event Action<Exception>? Failed;

    public DialogueRunner Runner => runner ?? throw new DialogueException(
        "Yarn dialogue backend has not been initialized.");

    public IDialogueVariableStore VariableStore => variableStore;

    public Task<DialogueCompletion> Completion => session.Completion;

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
        lineProcessorRegistered = false;
        presenterAdapter = new YarnDialoguePresenterAdapter(
            presenters,
            RequestHurryUp,
            RequestNextLine,
            HandleLinePresented,
            options =>
            {
                localized.RecordOptions(options);
                OptionsPresented?.Invoke(options);
            },
            ConvertPresentedLine)
        {
            Name = "MajoPresenterAdapter",
            PrepareLineAsync = preloader is null ? null : preloader.BeforeLineAsync,
            CommitLineBoundaryAsync = EstablishLineBoundaryAsync,
            PreparationChanged = preparing => PreparationChanged?.Invoke(preparing),
            BoundaryFailed = failure =>
            {
                session.RecordFailure(failure);
                Failed?.Invoke(failure);
            },
            OptionsStarting = preloader is null ? null : preloader.OptionsStarting,
        };
        variableStore.Name = "MajoDialogueVariables";
        runner.AddChild(variableStore);
        runner.VariableStorage = variableStore;
        runner.AddChild(presenterAdapter);
        runner.dialoguePresenters = new Godot.Collections.Array<Node?> { presenterAdapter };
        runner.AddCommandHandler(YarnNodeEntryTransform.CommandName, (Func<Task>)EnterPendingNodeAsync);
        runnerBridge = new YarnDialogueRunnerBridge(runner);
        owner.AddChild(runner);
        SetLocale(localeManager?.DialogueLocale ?? TranslationServer.GetLocale());

        nodeStarted = Callable.From<string>(HandleNodeStarted);
        nodeCompleted = Callable.From<string>(HandleNodeCompleted);
        dialogueCompleted = Callable.From(HandleDialogueCompleted);
        dialogueStarted = Callable.From(HandleDialogueStarted);
        runner.Connect(DialogueRunner.SignalName.onNodeStart, nodeStarted);
        runner.Connect(DialogueRunner.SignalName.onNodeComplete, nodeCompleted);
        runner.Connect(DialogueRunner.SignalName.onDialogueComplete, dialogueCompleted);
        runner.Connect(DialogueRunner.SignalName.onDialogueStart, dialogueStarted);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        session.ClearFailure();
        checkpoints.ClearPending();
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
        runnerBridge?.Dispose();
        runnerBridge = null;
        presenterAdapter?.ReleaseBoundaryResources();
        if (GodotObject.IsInstanceValid(runner))
        {
            runner!.QueueFree();
        }

        runner = null;
        presenterAdapter = null;
        checkpoints.ClearPending();
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
            project = await projectVersions.GetActiveAsync(projectId, cancellationToken);
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

        BeginProjectSession(projectId, startNode, resetCompletion: true);

        await StartProjectAsync(project, startNode, cancellationToken);
        projectVersions.MarkRunning(projectId, project);
    }

    private void BeginProjectSession(string projectId, string startNode, bool resetCompletion)
    {
        nodeCallDepth = nodeCompletions = 0;
        directionScopes?.ExitNode(currentNodeName);
        nodeEntryCancellation.Cancel();
        nodeEntryCancellation.Dispose();
        nodeEntryCancellation = new CancellationTokenSource();
        nodeEntryReason = checkpoints.Pending is not null ? NodeEntryReason.Restore :
            session.IsReplaying ? NodeEntryReason.InternalRebuild : NodeEntryReason.Normal;
        session.Begin(projectId, startNode, resetCompletion);
        currentNodeName = startNode;
        currentLineId = string.Empty;
        checkpoints.BeginSession();
        currentSubstitutions = Array.Empty<string>();
    }

    private async Task StartProjectAsync(
        YarnProject project,
        string startNode,
        CancellationToken cancellationToken)
    {
        variableStore.Program = project.Program;
        Runner.SetProject(project);
        if (!lineProcessorRegistered)
        {
            Runner.LineProvider.RegisterMarkerProcessor("br", localized.LineBreakMarkupReplacer);
            lineProcessorRegistered = true;
        }
        SetLocale(localized.Locale);
        presenterAdapter!.BeginSession();
        if (preloader is not null) await preloader.PrepareStartAsync(project,
            checkpoints.Pending?.Spot.Script.NodeName ?? startNode, cancellationToken, checkpoints.Pending?.Spot.LineId);
        cancellationToken.ThrowIfCancellationRequested();
        await Runner.StartDialogue(startNode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Runner.IsDialogueRunning && !Completion.IsCompleted)
        {
            throw new DialogueProjectException(
                session.ProjectId,
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

        nodeEntryCancellation.Cancel();
        preloader?.Clear();
        session.RequestStop();

        if (Runner.IsDialogueRunning)
        {
            var waitForBoundary = commandAdapter?.ActiveCommands.Count > 0;
            var stopBoundary = presenterAdapter!.PrepareToStop();
            commandAdapter?.CancelDialogue();
            if (commandAdapter is not null)
            {
                await commandAdapter.WaitForQuiescenceAsync(cancellationToken);
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

            if (presentationQuiescenceWaiter is not null)
            {
                await presentationQuiescenceWaiter(cancellationToken);
            }

            // DialogueRunner.Stop waits for dialogue-complete presenters, but
            // the RunLocalisedLine continuation that observed cancellation can
            // still retire on a later frame. Keep the adapter in stop-suppression
            // mode until that old line can no longer touch the next session's
            // cancellation source. Command dispatch needs the same boundary for
            // its final Continue.
            await YarnTask.NextFrame();
            await YarnTask.NextFrame();
        }
        else if (commandAdapter is not null)
        {
            await commandAdapter.WaitForQuiescenceAsync(cancellationToken);
        }

        if (!session.IsReplaying)
        {
            CompleteSession(DialogueCompletionKind.Stopped);
        }
    }

    public void RequestHurryUp()
    {
        if (isInitialized && !session.ProgressionFrozen)
        {
            if (presenterAdapter?.DeferAdvance(hurryUp: true) == true) return;
            Runner.RequestHurryUpLine();
        }
    }

    public void RequestNextLine()
    {
        if (isInitialized && !session.ProgressionFrozen &&
            presenterAdapter?.DeferAdvance() == true) return;
        if (isInitialized && !session.ProgressionFrozen && Runner.IsDialogueRunning &&
            session.TryRequestAdvance())
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
        if (adapter.Catalog is { } catalog) preloader?.SetCommandCatalog(catalog);
        if (adapter is YarnCommandAdapter yarnAdapter && preloader is not null)
            yarnAdapter.PrepareCommandAsync = preloader.BeforeCommandAsync;
    }

    public async Task ReplaceRunningProjectAsync(
        YarnProjectVersion version,
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin replayOrigin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureInitialized();
        if (!Runner.IsDialogueRunning && string.IsNullOrWhiteSpace(session.ProjectId))
        {
            throw new DialogueException("No running Yarn dialogue can be hot reloaded.");
        }

        session.SetProgressionFrozen(true);
        using var replay = session.EnterReplay();
        try
        {
            if (Runner.IsDialogueRunning)
            {
                await StopAsync(cancellationToken);
            }
            checkpoints.ReplacePending(checkpoint, replayOrigin);
            BeginProjectSession(
                checkpoint.Spot.Script.ProjectId,
                checkpoint.Spot.Script.NodeName,
                resetCompletion: false);
            await StartProjectAsync(
                version.Project,
                checkpoint.Spot.Script.NodeName,
                cancellationToken);
        }
        catch
        {
            checkpoints.ClearPending();
            throw;
        }
    }

    public void SetHotReloadProgressionFrozen(bool frozen) =>
        session.SetProgressionFrozen(frozen);

    public Task<ScriptDescriptor> ResolveAsync(ScriptId id, CancellationToken cancellationToken = default) =>
        checkpoints.ResolveAsync(id, cancellationToken);

    public ScriptCheckpoint CaptureCheckpoint() => CaptureCheckpointCore(false);

    private ScriptCheckpoint CaptureCheckpointCore(bool atEntry)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(currentNodeName) || string.IsNullOrWhiteSpace(currentLineId))
            throw new ScriptCheckpointException("Yarn playback is not waiting at a line boundary.");
        return checkpoints.Capture(Runner, new ScriptId(session.ProjectId, currentNodeName),
            currentLineId, currentSubstitutions, atEntry);
    }

    public Task ValidateCheckpointAsync(ScriptCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        checkpoints.ValidateCheckpointAsync(checkpoint, cancellationToken);

    public void PrepareRestore(ScriptCheckpoint checkpoint) => checkpoints.PrepareRestore(checkpoint);
    public void PrepareRestore(ScriptCheckpoint checkpoint, DialoguePresentationOrigin origin) =>
        checkpoints.PrepareRestore(checkpoint, origin);

    public async Task StopForReplayAsync(CancellationToken cancellationToken)
    {
        using var replay = session.EnterReplay();
        await StopAsync(cancellationToken);
    }

    public async Task ActivatePreparedReplayAsync(
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin origin,
        CancellationToken cancellationToken)
    {
        if (checkpoints.Pending is null)
            throw new ScriptCheckpointException("No prepared checkpoint replay is available.");
        using var replay = session.EnterReplay();
        var project = await projectVersions.GetActiveAsync(checkpoint.Spot.Script.ProjectId, cancellationToken);
        checkpoints.ReplacePending(checkpoint, origin);
        BeginProjectSession(checkpoint.Spot.Script.ProjectId, checkpoint.Spot.Script.NodeName, resetCompletion: false);
        await StartProjectAsync(project, checkpoint.Spot.Script.NodeName, cancellationToken);
        projectVersions.MarkRunning(checkpoint.Spot.Script.ProjectId, project);
    }

    public void CancelPreparedRestore(ScriptCheckpoint checkpoint) => checkpoints.CancelPreparedRestore(checkpoint);

    public void RegisterNativePresenter(Node presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        EnsureInitialized();
        if (presenter is not DialoguePresenterBase)
        {
            throw new DialogueException(
                $"Native presenter '{presenter.GetType().FullName}' does not implement " +
                $"{nameof(DialoguePresenterBase)}.");
        }

        if (presenterAdapter!.NativePresenters.Contains((DialoguePresenterBase)presenter))
            throw new DialogueException($"Native presenter '{presenter.Name}' is already registered.");
        presenterAdapter.NativePresenters.Add((DialoguePresenterBase)presenter);
    }

    public void UnregisterNativePresenter(Node presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        if (presenter is DialoguePresenterBase native) presenterAdapter?.NativePresenters.Remove(native);
    }

    public void SetPresentationQuiescenceWaiter(Func<CancellationToken, Task>? waiter) =>
        presentationQuiescenceWaiter = waiter;

    private void HandleNodeStarted(string nodeName)
    {
        var callDepth = checkpoints.GetCallStackDepth(Runner);
        // Return completes only the child; jump completes every unwound caller too.
        var returning = callDepth < nodeCallDepth && nodeCompletions == 1;
        nodeCallDepth = callDepth;
        nodeCompletions = 0;
        currentNodeName = nodeName;
        nodeActivation++;
        // Checkpoint replay skips the source prelude. Restore only the module here;
        // restoring stable state must never execute a normal scene boundary.
        if (nodeEntryReason != NodeEntryReason.Normal || returning)
            ApplyPendingModule(nodeName);
    }

    private async Task EnterPendingNodeAsync()
    {
        var activation = nodeActivation;
        if (enteredActivation == activation) return;
        var nodeName = currentNodeName;
        var token = nodeEntryCancellation.Token;
        try
        {
            if (session.Failure is { } boundaryFailure) throw boundaryFailure;
            var module = FindModule(nodeName);
            if (module is not null && module.Diagnostics.Any(value =>
                value.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Node '{nodeName}' has invalid Direction metadata.");
            if (BeforeNodeEntryAsync is { } enter)
                await enter(new NodeEntryRequest(session.ProjectId, nodeName, activation, module, nodeEntryReason), token);
            token.ThrowIfCancellationRequested();
            if (activation != nodeActivation) return;
            ApplyPendingModule(nodeName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            session.RecordFailure(error);
            Failed?.Invoke(error);
            await Runner.Stop();
        }
    }

    private NodeDirectionModule? FindModule(string nodeName) =>
        projectVersions.GetActiveVersion(session.ProjectId)?.DirectionModules
            .FirstOrDefault(candidate => candidate.NodeId.Equals(nodeName, StringComparison.Ordinal));

    private void ApplyPendingModule(string nodeName)
    {
        if (enteredActivation == nodeActivation) return;
        var module = FindModule(nodeName);
        if (module is not null) directionScopes?.ApplyModule(module);
        else directionScopes?.EnterNode(nodeName);
        if (Runner.YarnProject is { } project) preloader?.EnterNode(project, nodeName);
        enteredActivation = nodeActivation;
        NodeEntered?.Invoke(nodeName);
        nodeEntryReason = NodeEntryReason.Normal;
    }

    private void HandleNodeCompleted(string nodeName)
    {
        nodeCompletions++;
        try { NodeExited?.Invoke(nodeName); }
        catch (Exception error)
        {
            session.RecordFailure(error);
            Failed?.Invoke(error);
        }
        finally { directionScopes?.ExitNode(nodeName); }
    }

    private void HandleDialogueCompleted()
    {
        preloader?.Clear();
        if (session.IsReplaying)
        {
            return;
        }
        projectVersions.EndSession(session.ProjectId);
        if (session.Failure is not null)
        {
            session.Fault();
            return;
        }

        CompleteSession(session.IsStopping ? DialogueCompletionKind.Stopped : DialogueCompletionKind.Completed);
    }

    private void HandleDialogueStarted()
    {
        if (checkpoints.RestoreSelectedNode(Runner) is { } depth) nodeCallDepth = depth;
    }

    private LineBoundaryContext? activeBoundary;
    public Task<IDisposable> AcquireRestoreAsync(CancellationToken token) =>
        presenterAdapter!.AcquireRestoreAsync(token);
    public Func<ScriptCheckpoint, DialoguePresentationOrigin, CancellationToken, Task>? ResumeCheckpointAsync { get; set; }
    public Task ResumeBoundaryAsync(
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin origin,
        CancellationToken token) =>
        ResumeCheckpointAsync?.Invoke(checkpoint, origin, token) ?? throw new ScriptCheckpointException(
            "Script playback has not attached checkpoint recovery.");
    public bool CanReviseBoundary(LineBoundaryContext boundary) =>
        presenterAdapter?.CanReviseBoundary(boundary) == true;
    public bool IsCurrentBoundary(LineBoundaryContext boundary) => ReferenceEquals(activeBoundary, boundary);
    public Task WaitForPreparationAsync(CancellationToken token) =>
        presenterAdapter?.WaitForPreparationAsync(token) ?? Task.CompletedTask;
    public event Action<bool>? PreparationChanged;

    public Func<LineBoundaryContext, CancellationToken, Task>? CommitLineBoundaryAsync { get; set; }

    private async Task EstablishLineBoundaryAsync(
        DialogueLine line,
        IReadOnlyList<string> substitutions,
        long generation,
        long sequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        currentLineId = line.Id;
        currentSubstitutions = substitutions;
        localized.RecordLine(line);
        session.ResetAdvance();
        CaptureCheckpointCore(true);
        if (CommitLineBoundaryAsync is not null)
        {
            activeBoundary = new LineBoundaryContext(generation, sequence, CaptureCheckpoint(), line);
            try { await CommitLineBoundaryAsync(activeBoundary, cancellationToken); }
            finally { activeBoundary = null; }
        }
    }

    private void HandleLinePresented(
        DialogueLine line,
        IReadOnlyList<string> substitutions)
    {
        currentLineId = line.Id;
        currentSubstitutions = substitutions;
        localized.RecordLine(line);
        session.ResetAdvance();
        LinePresented?.Invoke(line);
        if (line.Origin == DialoguePresentationOrigin.HotReloadReplay &&
            Runner.IsDialogueRunning)
        {
            Runner.RequestHurryUpLine();
        }
    }

    public DialogueLine? ReResolveCurrentLine(string locale, IReadOnlyList<string> fallbackChain) =>
        localized.ReResolveCurrentLine(runner?.YarnProject, currentLineId, currentSubstitutions, locale, fallbackChain);

    public IReadOnlyList<DialogueOption> ReResolveCurrentOptions(string locale, IReadOnlyList<string> fallbackChain) =>
        localized.ReResolveCurrentOptions(runner?.YarnProject, locale, fallbackChain);

    public void SetLocale(string locale) => localized.SetLocale(locale, runner);

    public Task WaitForLocaleSwitchBoundaryAsync(CancellationToken cancellationToken)
    {
        if (runner?.IsDialogueRunning != true || commandAdapter?.HasActiveCommands != true)
        {
            return Task.CompletedTask;
        }

        return presenterAdapter!.WaitForPresentationBoundaryAsync(cancellationToken);
    }

    private DialogueLine ConvertPresentedLine(LocalizedLine line) =>
        localized.ConvertPresentedLine(line, checkpoints.ConsumePresentationOrigin(), runner);

    private void HandleCommandFailed(RuntimeCommandException failure)
    {
        if (failure.IsRecoverable &&
            commandAdapter?.AbortOnRecoverableFailure != true)
        {
            return;
        }

        session.RecordFailure(failure);

        Failed?.Invoke(failure);
    }

    private void CompleteSession(DialogueCompletionKind kind)
    {
        directionScopes?.ExitNode(currentNodeName);
        session.Complete(kind);
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


        if (Runner.IsConnected(DialogueRunner.SignalName.onDialogueStart, dialogueStarted))
        {
            Runner.Disconnect(DialogueRunner.SignalName.onDialogueStart, dialogueStarted);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new DialogueException("Yarn dialogue backend has not been initialized.");
        }
    }

    public void Dispose()
    {
        runnerBridge?.Dispose();
        nodeEntryCancellation.Cancel();
        nodeEntryCancellation.Dispose();
        preloader?.Dispose();
        presenterAdapter?.Dispose();
        if (GodotObject.IsInstanceValid(runner))
        {
            runner.QueueFree();
            runner.Dispose();
        }

        variableStore.Dispose();
    }
}

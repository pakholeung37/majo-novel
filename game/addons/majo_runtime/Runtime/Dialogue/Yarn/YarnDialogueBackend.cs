#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Majo.Runtime.Script;
using Majo.YarnBridge;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Resources;
using Majo.Runtime.Localization;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnDialogueBackend : IYarnDialogueBackend,
    IScriptCatalogBackend,
    IScriptCheckpointBackend
{
    private readonly Node owner;
    private readonly IResourceLoader<YarnProject> projects;
    private readonly IReadOnlyList<IDialoguePresenter> presenters;
    private readonly ILocaleManager? localeManager;
    private readonly YarnVmStateBridge stateBridge = new();
    private readonly YarnDialogueVariableStore variableStore = new();
    private readonly object sync = new();
    private DialogueRunner? runner;
    private YarnDialoguePresenterAdapter? presenterAdapter;
    private IYarnCommandAdapter? commandAdapter;
    private Callable nodeStarted;
    private Callable nodeCompleted;
    private Callable dialogueCompleted;
    private Callable dialogueStarted;
    private TaskCompletionSource<DialogueCompletion> completion = CompletedSource();
    private string currentProjectId = string.Empty;
    private string currentStartNode = string.Empty;
    private string currentNodeName = string.Empty;
    private string currentLineId = string.Empty;
    private IReadOnlyList<string> currentSubstitutions = Array.Empty<string>();
    private DialogueLine? currentLine;
    private IReadOnlyList<DialogueOption> currentOptions = Array.Empty<DialogueOption>();
    private ScriptCheckpoint? pendingRestore;
    private Exception? pendingFailure;
    private bool isStopping;
    private bool isInitialized;
    private int nextLineRequested;
    private string configuredLocale = "en";
    private Func<CancellationToken, Task>? presentationQuiescenceWaiter;

    public YarnDialogueBackend(
        Node owner,
        IResourceManager resources,
        string scriptsPath,
        IReadOnlyList<IDialoguePresenter> presenters,
        ILocaleManager? localeManager = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(resources);
        projects = resources.CreateLoader<YarnProject>(scriptsPath);
        this.presenters = presenters ?? throw new ArgumentNullException(nameof(presenters));
        this.localeManager = localeManager;
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
            RequestHurryUp,
            RequestNextLine,
            HandleLinePresented,
            options =>
            {
                currentOptions = options;
                OptionsPresented?.Invoke(options);
            },
            ConvertPresentedLine)
        {
            Name = "MajoPresenterAdapter",
        };
        variableStore.Name = "MajoDialogueVariables";
        runner.AddChild(variableStore);
        runner.VariableStorage = variableStore;
        runner.AddChild(presenterAdapter);
        runner.dialoguePresenters = new Godot.Collections.Array<Node?> { presenterAdapter };
        owner.AddChild(runner);
        configuredLocale = localeManager?.DialogueLocale ?? TranslationServer.GetLocale();

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
        pendingFailure = null;
        pendingRestore = null;
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
        pendingRestore = null;
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
            currentNodeName = startNode;
            currentLineId = string.Empty;
            currentSubstitutions = Array.Empty<string>();
            pendingFailure = null;
            isStopping = false;
            completion = new TaskCompletionSource<DialogueCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        variableStore.Program = project.Program;
        Runner.SetProject(project);
        SetLocale(configuredLocale);
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
        if (isInitialized && Runner.IsDialogueRunning &&
            Interlocked.Exchange(ref nextLineRequested, 1) == 0)
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

    public async Task<ScriptDescriptor> ResolveAsync(
        ScriptId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        YarnProject project;
        try
        {
            project = await projects.LoadAsync(
                id.ProjectId,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ScriptRuntimeException(
                $"Failed to load script project '{id.ProjectId}'.",
                exception);
        }

        return CreateDescriptor(project, id);
    }

    public ScriptCheckpoint CaptureCheckpoint()
    {
        EnsureInitialized();
        var project = Runner.YarnProject ?? throw new ScriptCheckpointException(
            "No Yarn project is active.");
        if (string.IsNullOrWhiteSpace(currentNodeName) ||
            string.IsNullOrWhiteSpace(currentLineId))
        {
            throw new ScriptCheckpointException(
                "Yarn playback is not waiting at a line boundary.");
        }

        var id = new ScriptId(currentProjectId, currentNodeName);
        var descriptor = CreateDescriptor(project, id);
        var snapshot = stateBridge.CaptureAtLine(
            Runner.Dialogue,
            currentLineId,
            currentSubstitutions);
        return new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            descriptor.ProgramFingerprint,
            new ScriptPlaybackSpot(id, snapshot.LineId),
            snapshot.ResolvedSubstitutions);
    }

    public async Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.FormatVersion != ScriptCheckpoint.CurrentFormatVersion)
        {
            throw new ScriptCheckpointException(
                $"Unsupported script checkpoint format '{checkpoint.FormatVersion}'; " +
                $"expected '{ScriptCheckpoint.CurrentFormatVersion}'.");
        }

        if (checkpoint.Spot.Boundary != ScriptCheckpointBoundary.AwaitingLineAdvance)
        {
            throw new ScriptCheckpointException(
                $"Unsupported script checkpoint boundary '{checkpoint.Spot.Boundary}'.");
        }

        var descriptor = await ResolveAsync(checkpoint.Spot.Script, cancellationToken);
        if (!string.Equals(
                descriptor.ProgramFingerprint,
                checkpoint.ProgramFingerprint,
                StringComparison.Ordinal))
        {
            throw new ScriptCheckpointException(
                $"Script '{checkpoint.Spot.Script}' no longer matches checkpoint fingerprint.");
        }

        if (!descriptor.LineIds.Contains(checkpoint.Spot.LineId, StringComparer.Ordinal))
        {
            throw new ScriptCheckpointException(
                $"Line '{checkpoint.Spot.LineId}' does not exist in script " +
                $"'{checkpoint.Spot.Script}'.");
        }
    }

    public void PrepareRestore(ScriptCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (pendingRestore is not null)
        {
            throw new ScriptCheckpointException(
                "Another script checkpoint restore is already pending.");
        }

        pendingRestore = checkpoint;
    }

    public void CancelPreparedRestore(ScriptCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (ReferenceEquals(pendingRestore, checkpoint) || pendingRestore == checkpoint)
        {
            pendingRestore = null;
        }
    }

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

        Runner.dialoguePresenters ??= new Godot.Collections.Array<Node?>();
        if (Runner.dialoguePresenters.Contains(presenter))
        {
            throw new DialogueException(
                $"Native presenter '{presenter.Name}' is already registered.");
        }

        Runner.dialoguePresenters.Add(presenter);
    }

    public void UnregisterNativePresenter(Node presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        if (!isInitialized || Runner.dialoguePresenters is null)
        {
            return;
        }

        Runner.dialoguePresenters.Remove(presenter);
    }

    public void SetPresentationQuiescenceWaiter(Func<CancellationToken, Task>? waiter) =>
        presentationQuiescenceWaiter = waiter;

    private void HandleNodeStarted(string nodeName)
    {
        currentNodeName = nodeName;
        NodeEntered?.Invoke(nodeName);
    }

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

    private void HandleDialogueStarted()
    {
        if (pendingRestore is not { } checkpoint)
        {
            return;
        }

        pendingRestore = null;

        var project = Runner.YarnProject ?? throw new ScriptCheckpointException(
            "DialogueRunner has no Yarn project during checkpoint restore.");
        var bridgeSnapshot = new YarnVmLineSnapshot(
            checkpoint.Spot.Script.NodeName,
            checkpoint.Spot.LineId,
            0,
            "WaitingForContinue",
            0,
            0,
            0,
            checkpoint.ResolvedSubstitutions);
        stateBridge.InjectReplayAfterNodeSelected(
            Runner.Dialogue,
            project.Program,
            bridgeSnapshot);
    }

    private void HandleLinePresented(
        DialogueLine line,
        IReadOnlyList<string> substitutions)
    {
        currentLineId = line.Id;
        currentSubstitutions = substitutions;
        currentLine = line;
        currentOptions = Array.Empty<DialogueOption>();
        Volatile.Write(ref nextLineRequested, 0);
        LinePresented?.Invoke(line);
    }

    public DialogueLine? ReResolveCurrentLine(string locale, IReadOnlyList<string> fallbackChain)
    {
        if (string.IsNullOrWhiteSpace(currentLineId) || runner?.YarnProject is null)
        {
            return null;
        }

        var project = runner.YarnProject;
        string? rawText = null;
        string? resolvedLocale = null;

        foreach (var candidate in fallbackChain)
        {
            var translated = ResolveExactGodotTranslation(currentLineId, candidate);
            if (translated is not null)
            {
                rawText = translated;
                resolvedLocale = candidate;
                break;
            }
        }

        if (rawText is null && project.baseLocalization is not null)
        {
            rawText = project.baseLocalization.GetLocalizedString(currentLineId);
            resolvedLocale = project.baseLocalization.LocaleCode;
        }

        if (rawText is null)
        {
            return currentLine;
        }

        var localized = CreateLocalizedLine(
            currentLineId,
            rawText,
            currentSubstitutions,
            project.LineMetadata?.GetMetadata(currentLineId) ?? Array.Empty<string>(),
            locale);
        var reResolved = YarnLineConverter.Convert(localized, resolvedLocale);

        currentLine = reResolved;
        return reResolved;
    }

    public IReadOnlyList<DialogueOption> ReResolveCurrentOptions(string locale, IReadOnlyList<string> fallbackChain)
    {
        if (currentOptions.Count == 0 || runner?.YarnProject is null)
        {
            return currentOptions;
        }

        var project = runner.YarnProject;
        var list = new List<DialogueOption>(currentOptions.Count);
        foreach (var option in currentOptions)
        {
            var lineId = option.Line.Id;
            string? rawText = null;
            string? resolvedLocale = null;
            foreach (var candidate in fallbackChain)
            {
                var translated = ResolveExactGodotTranslation(lineId, candidate);
                if (translated is not null)
                {
                    rawText = translated;
                    resolvedLocale = candidate;
                    break;
                }
            }

            if (rawText is null && project.baseLocalization is not null)
            {
                rawText = project.baseLocalization.GetLocalizedString(lineId);
                resolvedLocale = project.baseLocalization.LocaleCode;
            }

            if (rawText is not null)
            {
                var metadata = project.LineMetadata?.GetMetadata(lineId) ?? Array.Empty<string>();
                var substitutions = option.Substitutions ?? Array.Empty<string>();
                var localized = CreateLocalizedLine(lineId, rawText, substitutions, metadata, locale);
                var newLine = YarnLineConverter.Convert(localized, resolvedLocale);
                list.Add(new DialogueOption(option.Id, newLine, option.IsAvailable, substitutions));
            }
            else
            {
                list.Add(option);
            }
        }

        currentOptions = list.AsReadOnly();
        return currentOptions;
    }

    public void SetLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        configuredLocale = LocaleManager.NormalizeLocale(locale);
        if (runner?.YarnProject is not null)
        {
            if (runner.LineProvider is LineProviderBehaviour provider)
            {
                provider.LocaleCode = configuredLocale;
            }
        }
    }

    public Task WaitForLocaleSwitchBoundaryAsync(CancellationToken cancellationToken)
    {
        if (runner?.IsDialogueRunning != true || commandAdapter?.HasActiveCommands != true)
        {
            return Task.CompletedTask;
        }

        return presenterAdapter!.WaitForPresentationBoundaryAsync(cancellationToken);
    }

    private DialogueLine ConvertPresentedLine(LocalizedLine line) =>
        YarnLineConverter.Convert(line, ResolvePresentedLocale(line));

    private string ResolvePresentedLocale(LocalizedLine line)
    {
        var requested = localeManager?.DialogueLocale ?? runner?.LineProvider.LocaleCode ?? "en";
        var chain = localeManager?.GetFallbackChain(requested) ?? [requested];
        foreach (var candidate in chain)
        {
            var translated = ResolveExactGodotTranslation(line.TextID, candidate);
            if (translated is not null &&
                string.Equals(translated, line.RawText, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return runner?.YarnProject?.baseLocalization?.LocaleCode ?? requested;
    }

    private static string? ResolveExactGodotTranslation(string lineId, string locale)
    {
        foreach (var translation in TranslationServer.FindTranslations(locale, exact: true))
        {
            var value = translation.GetMessage(new StringName(lineId)).ToString();
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, lineId, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static LocalizedLine CreateLocalizedLine(
        string lineId,
        string rawText,
        IReadOnlyList<string> substitutions,
        IReadOnlyList<string> metadata,
        string parserLocale)
    {
        var expanded = LineParser.ExpandSubstitutions(rawText, substitutions.ToArray());
        var parser = new LineParser();
        return new LocalizedLine
        {
            TextID = lineId,
            RawText = rawText,
            Substitutions = substitutions.ToArray(),
            Metadata = metadata.ToArray(),
            Text = parser.ParseString(expanded, parserLocale),
        };
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


        if (Runner.IsConnected(DialogueRunner.SignalName.onDialogueStart, dialogueStarted))
        {
            Runner.Disconnect(DialogueRunner.SignalName.onDialogueStart, dialogueStarted);
        }
    }

    private static ScriptDescriptor CreateDescriptor(YarnProject project, ScriptId id)
    {
        if (project.Program is null)
        {
            throw new ScriptRuntimeException(
                $"Script project '{id.ProjectId}' has no compiled program.");
        }

        if (!project.Program.Nodes.TryGetValue(id.NodeName, out var node))
        {
            throw new ScriptRuntimeException(
                $"Node '{id.NodeName}' was not found in project '{id.ProjectId}'.");
        }

        var allLineIds = project.Program.Nodes.Values
            .SelectMany(programNode => programNode.Instructions)
            .Where(instruction => instruction.InstructionTypeCase ==
                global::Yarn.Instruction.InstructionTypeOneofCase.RunLine)
            .Select(instruction => instruction.RunLine.LineID)
            .ToArray();
        var duplicate = allLineIds.GroupBy(lineId => lineId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ScriptCheckpointException(
                $"Project '{id.ProjectId}' contains duplicate line ID '{duplicate.Key}'.");
        }

        var lineIds = node.Instructions
            .Where(instruction => instruction.InstructionTypeCase ==
                global::Yarn.Instruction.InstructionTypeOneofCase.RunLine)
            .Select(instruction => instruction.RunLine.LineID)
            .ToArray();
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(project.Program.ToByteArray())).ToLowerInvariant();
        return new ScriptDescriptor(id, fingerprint, lineIds);
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

#nullable enable

using System.Reflection;
using Majo.Runtime;
using Majo.Runtime.Dialogue;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DialoguePlayerTests
{
    [Fact]
    public async Task StartUsesDefaultsAndReturnsWhenBackendIsRunning()
    {
        var backend = new FakeDialogueBackend { DelayStart = true };
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        var states = new List<DialogueSessionState>();
        player.StateChanged += change => states.Add(change.Current);

        var start = player.StartAsync();
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DialogueSessionState.Loading, player.State);
        Assert.False(start.IsCompleted);
        Assert.Equal("main", backend.ProjectId);
        Assert.Equal("Start", backend.StartNode);

        backend.AllowStart.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DialogueSessionState.Running, player.State);
        Assert.Equal("main", player.CurrentProjectId);
        Assert.Equal(
            new[] { DialogueSessionState.Loading, DialogueSessionState.Running },
            states);
    }

    [Fact]
    public async Task NaturalCompletionReturnsPlayerToIdle()
    {
        var backend = new FakeDialogueBackend();
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        await player.StartAsync(new DialogueStartRequest("chapter", "Opening"));
        var completion = player.Completion;
        DialogueCompletion? observed = null;
        DialogueCompletion? stopped = null;
        player.Completed += result => observed = result;
        player.Stopped += result => stopped = result;

        backend.EnterNode("Opening");
        backend.PresentLine(CreateLine("line:opening"));
        backend.Complete(DialogueCompletionKind.Completed);
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DialogueCompletionKind.Completed, result.Kind);
        Assert.Equal(DialogueSessionState.Idle, player.State);
        Assert.Equal("Opening", player.CurrentNode);
        Assert.Equal("line:opening", player.CurrentLineId);
        Assert.Equal(result, observed);
        Assert.Null(stopped);
    }

    [Fact]
    public async Task StartingWhileRunningIsRejected()
    {
        var backend = new FakeDialogueBackend();
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        await player.StartAsync();

        var exception = await Assert.ThrowsAsync<DialogueStateException>(async () =>
            await player.StartAsync());

        Assert.Equal(DialogueSessionState.Running, exception.State);
        await player.StopAsync();
    }

    [Fact]
    public async Task StopCancelsLoadingAndCompletesAsStopped()
    {
        var backend = new FakeDialogueBackend { DelayStart = true };
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        var start = player.StartAsync();
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var completion = player.Completion;
        DialogueCompletion? stopped = null;
        DialogueCompletion? completed = null;
        player.Stopped += result => stopped = result;
        player.Completed += result => completed = result;

        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await start.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DialogueCompletionKind.Stopped, result.Kind);
        Assert.Equal("Start", result.StartNode);
        Assert.Equal(DialogueSessionState.Idle, player.State);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(result, stopped);
        Assert.Null(completed);
    }

    [Fact]
    public async Task SessionEventsAndOptionPhaseExposeResolvedState()
    {
        var backend = new FakeDialogueBackend();
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        DialogueStartRequest? started = null;
        player.Started += request => started = request;

        await player.StartAsync();
        var options = new[]
        {
            new DialogueOption(1, CreateLine("line:choice"), true),
        };
        backend.PresentOptions(options);

        Assert.Equal(new DialogueStartRequest("main", "Start"), started);
        Assert.True(player.IsPresentingOptions);
        Assert.Equal(options, player.CurrentOptions);

        backend.PresentLine(CreateLine("line:after-choice"));
        Assert.False(player.IsPresentingOptions);
        Assert.Empty(player.CurrentOptions);
        await player.StopAsync();
    }

    [Fact]
    public async Task StopDuringCustomNodeLoadingReportsRequestedStartNode()
    {
        var backend = new FakeDialogueBackend { DelayStart = true };
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        var start = player.StartAsync(new DialogueStartRequest("chapter", "SceneTwo"));
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("SceneTwo", (await player.Completion).StartNode);
    }

    [Fact]
    public async Task ResetStopsRunningSessionAndClearsPublishedState()
    {
        var backend = new FakeDialogueBackend();
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        await player.StartAsync();
        backend.EnterNode("Start");
        backend.PresentLine(CreateLine("line:before-reset"));

        await player.ResetAsync(CancellationToken.None);

        Assert.Equal(DialogueSessionState.Idle, player.State);
        Assert.Null(player.CurrentProjectId);
        Assert.Null(player.CurrentNode);
        Assert.Null(player.CurrentLineId);
        Assert.False(player.IsPresentingOptions);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task StateChangedCanRequestStopWithoutReenteringPlayerLock()
    {
        var backend = new FakeDialogueBackend { DelayStart = true };
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        Task? stopping = null;
        player.StateChanged += change =>
        {
            if (change.Current == DialogueSessionState.Loading)
            {
                stopping = player.StopAsync();
            }
        };

        var starting = player.StartAsync();
        await starting.WaitAsync(TimeSpan.FromSeconds(1));
        await stopping!.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DialogueSessionState.Idle, player.State);
        Assert.Equal(DialogueCompletionKind.Stopped, (await player.Completion).Kind);
    }

    [Fact]
    public async Task BackendFailureFaultsCurrentSessionAndCanRecover()
    {
        var backend = new FakeDialogueBackend();
        var player = new DialoguePlayer(backend, "main", "Start");
        await player.InitializeAsync(CancellationToken.None);
        await player.StartAsync();
        var completion = player.Completion;
        var failure = new InvalidOperationException("backend failed");

        backend.Fail(failure);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(failure, observed);
        Assert.Equal(DialogueSessionState.Faulted, player.State);

        await player.StartAsync(new DialogueStartRequest("recovery", "Start"));
        Assert.Equal(DialogueSessionState.Running, player.State);
        await player.StopAsync();
    }

    [Fact]
    public async Task DialogueVariablesHideYarnPrefixAndPreserveTypes()
    {
        var store = new FakeVariableStore();
        var variables = new DialogueVariables(store);
        var changes = new List<DialogueVariableChanged>();
        variables.Changed += changes.Add;
        await variables.InitializeAsync(CancellationToken.None);

        variables.SetNumber("score", 3.5);
        variables.SetString("$route", "evidence");
        variables.SetBoolean("pressed", true);

        Assert.Equal(3.5, variables.Get("score").NumberValue);
        Assert.Equal("evidence", variables.Get("route").StringValue);
        Assert.True(variables.Get("$pressed").BooleanValue);
        Assert.Equal(new[] { "score", "route", "pressed" }, changes.Select(x => x.Name));

        await variables.ResetAsync(CancellationToken.None);
        Assert.False(variables.Contains("score"));
    }

    [Fact]
    public void StableDialogueApiDoesNotExposeYarnTypes()
    {
        var assembly = typeof(IDialoguePlayer).Assembly;
        var types = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Majo.Runtime.Dialogue")
            .Append(typeof(MajoRuntimeBootstrap));

        foreach (var type in types)
        {
            foreach (var memberType in GetPublicMemberTypes(type))
            {
                Assert.False(
                    IsYarnType(memberType),
                    $"Public member on {type.FullName} exposes Yarn type {memberType.FullName}.");
            }
        }
    }

    private static IEnumerable<Type> GetPublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var eventInfo in type.GetEvents(flags))
        {
            if (eventInfo.EventHandlerType is not null)
            {
                yield return eventInfo.EventHandlerType;
            }
        }
    }

    private static bool IsYarnType(Type type)
    {
        if (type.IsArray)
        {
            return IsYarnType(type.GetElementType()!);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(IsYarnType))
        {
            return true;
        }

        var namespaceName = type.Namespace ?? string.Empty;
        return namespaceName == "Yarn" ||
            namespaceName.StartsWith("Yarn.", StringComparison.Ordinal) ||
            namespaceName == "YarnSpinnerGodot" ||
            namespaceName.StartsWith("YarnSpinnerGodot.", StringComparison.Ordinal);
    }

    private static DialogueLine CreateLine(string id) => new(
        id,
        "Narrator",
        new DialogueText("Text", Array.Empty<DialogueMarkupAttribute>()),
        Array.Empty<string>());

    private sealed class FakeDialogueBackend : IDialogueBackend
    {
        private TaskCompletionSource<DialogueCompletion> completion = NewCompletion();

        public event Action<string>? NodeEntered;

        public event Action<string>? NodeExited;

        public event Action<DialogueLine>? LinePresented;

        public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

        public event Action<Exception>? Failed;

        public TaskCompletionSource StartEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStart { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DelayStart { get; init; }

        public string? ProjectId { get; private set; }

        public string? StartNode { get; private set; }

        public int StopCount { get; private set; }

        public Task<DialogueCompletion> Completion => completion.Task;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StartAsync(
            string projectId,
            string startNode,
            CancellationToken cancellationToken)
        {
            ProjectId = projectId;
            StartNode = startNode;
            completion = NewCompletion();
            StartEntered.TrySetResult();
            if (DelayStart)
            {
                await AllowStart.Task.WaitAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.CompletedTask;
        }

        public void RequestHurryUp()
        {
        }

        public void RequestNextLine()
        {
        }

        public void EnterNode(string node) => NodeEntered?.Invoke(node);

        public void ExitNode(string node) => NodeExited?.Invoke(node);

        public void PresentLine(DialogueLine line) => LinePresented?.Invoke(line);

        public void PresentOptions(IReadOnlyList<DialogueOption> options) =>
            OptionsPresented?.Invoke(options);

        public void Complete(DialogueCompletionKind kind) => completion.TrySetResult(
            new DialogueCompletion(kind, ProjectId!, StartNode!));

        public void Fail(Exception exception)
        {
            Failed?.Invoke(exception);
            completion.TrySetException(exception);
        }

        private static TaskCompletionSource<DialogueCompletion> NewCompletion() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeVariableStore : IDialogueVariableStore
    {
        private readonly Dictionary<string, DialogueValue> values = new();

        public event Action<DialogueVariableChanged>? Changed;

        public bool Contains(string name) => values.ContainsKey(name);

        public bool TryGet(string name, out DialogueValue value) =>
            values.TryGetValue(name, out value);

        public void Set(string name, DialogueValue value)
        {
            values[name] = value;
            Changed?.Invoke(new DialogueVariableChanged(name, value));
        }

        public void Clear() => values.Clear();
    }
}

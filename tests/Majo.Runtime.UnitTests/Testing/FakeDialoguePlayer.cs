#nullable enable

using Majo.Runtime.Dialogue;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class FakeDialoguePlayer : IDialoguePlayer
{
    private readonly TaskCompletionSource nextLineRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string>? onCall;

    public FakeDialoguePlayer(Action<string>? onCall = null)
    {
        this.onCall = onCall;
    }

    public FakeDialoguePlayer(List<string> order) : this(order.Add)
    {
    }

    public event Action<DialogueStateChanged>? StateChanged;
    public event Action<DialogueStartRequest>? Started;
    public event Action<DialogueLine>? LinePresented;
    public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented { add { } remove { } }
    public event Action<DialogueCompletion>? Completed;
    public event Action<DialogueCompletion>? Stopped;
    public event Action<Exception>? Failed;

    public DialogueSessionState State { get; set; } = DialogueSessionState.Idle;
    public string? CurrentProjectId { get; set; } = "test";
    public string? CurrentNode { get; set; } = "Start";
    public string? CurrentLineId { get; set; }
    public bool IsPresentingOptions { get; set; }
    public IReadOnlyList<DialogueOption> CurrentOptions { get; set; } = [];
    public Task<DialogueCompletion> Completion => Task.FromResult(
        new DialogueCompletion(DialogueCompletionKind.Completed, CurrentProjectId ?? "main", CurrentNode ?? "Start"));

    public int NextLineRequests { get; private set; }
    public int HurryUpRequests { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(
        DialogueStartRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        State = DialogueSessionState.Running;
        onCall?.Invoke("start");
        if (request is not null)
        {
            CurrentProjectId = request.ProjectId;
            CurrentNode = request.StartNode;
            Started?.Invoke(request);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        onCall?.Invoke("stop");
        State = DialogueSessionState.Idle;
        Stopped?.Invoke(new DialogueCompletion(DialogueCompletionKind.Stopped, CurrentProjectId ?? "main", CurrentNode ?? "Start"));
        return Task.CompletedTask;
    }

    public void RequestHurryUp() => HurryUpRequests++;

    public void RequestNextLine()
    {
        NextLineRequests++;
        nextLineRequested.TrySetResult();
    }

    public Task WaitForNextLineRequestAsync() =>
        NextLineRequests > 0 ? Task.CompletedTask : nextLineRequested.Task;

    public void Present(DialogueLine line)
    {
        CurrentLineId = line.Id;
        LinePresented?.Invoke(line);
    }

    public void Present(string project, string node, DialogueLine line)
    {
        CurrentProjectId = project;
        CurrentNode = node;
        CurrentLineId = line.Id;
        LinePresented?.Invoke(line);
    }

    public void EmitLine(DialogueLine line) => Present(line);

    public void SetState(DialogueSessionState state)
    {
        var previous = State;
        State = state;
        StateChanged?.Invoke(new DialogueStateChanged(previous, state));
    }

    public void FireCompleted(DialogueCompletion completion) => Completed?.Invoke(completion);

    public void FireFailed(Exception ex) => Failed?.Invoke(ex);
}

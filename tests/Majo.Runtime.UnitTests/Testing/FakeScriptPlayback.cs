#nullable enable

using Majo.Runtime.Script;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class FakeScriptPlayback : IScriptPlayback
{
    public event Action<ScriptPlaybackPhaseChanged>? PhaseChanged;

    public ScriptPlaybackPhase Phase { get; private set; } = ScriptPlaybackPhase.Idle;
    public ScriptPlaybackSpot? Spot { get; set; }
    public ScriptPlaybackSpot? CurrentSpot => Spot;
    public bool CanCaptureCheckpoint => Phase == ScriptPlaybackPhase.AwaitingAdvance;

    public ScriptCheckpoint? LastCapturedCheckpoint { get; private set; }
    public Exception? ValidationFailure { get; set; }
    public Exception? RestoreFailure { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ScriptCheckpoint CaptureCheckpoint()
    {
        LastCapturedCheckpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            "test-fingerprint",
            CurrentSpot ?? new ScriptPlaybackSpot(new ScriptId("test", "Start"), "line:1"));
        return LastCapturedCheckpoint;
    }

    public Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (ValidationFailure is not null)
        {
            return Task.FromException(ValidationFailure);
        }
        return Task.CompletedTask;
    }

    public Task RestoreCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (RestoreFailure is not null)
        {
            return Task.FromException(RestoreFailure);
        }
        Spot = checkpoint.Spot;
        return Task.CompletedTask;
    }

    public void SetPhase(ScriptPlaybackPhase phase)
    {
        var previous = Phase;
        Phase = phase;
        PhaseChanged?.Invoke(new ScriptPlaybackPhaseChanged(previous, phase));
    }

    public void SetLine(
        string project,
        string node,
        string line,
        ScriptPlaybackPhase phase)
    {
        Phase = phase;
        Spot = new ScriptPlaybackSpot(new ScriptId(project, node), line);
        PhaseChanged?.Invoke(new ScriptPlaybackPhaseChanged(Phase, phase));
    }
}

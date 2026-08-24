using System.Reflection;
using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.History;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ScriptRuntimeTests
{
    [Fact]
    public async Task RestoreCheckpoint_ValidatesBeforeStoppingOrPreparingRestore()
    {
        var order = new List<string>();
        var dialogue = new FakeDialoguePlayer(order);
        var checkpointBackend = new FakeCheckpointBackend(order)
        {
            ValidationFailure = new ScriptCheckpointException("invalid checkpoint"),
        };
        var playback = new ScriptPlayback(
            dialogue,
            new FakeDialoguePresentation(),
            checkpointBackend);
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            "fingerprint",
            new ScriptPlaybackSpot(new ScriptId("project", "Start"), "line:1"));

        await Assert.ThrowsAsync<ScriptCheckpointException>(() =>
            playback.RestoreCheckpointAsync(checkpoint));

        Assert.Equal(["validate"], order);
    }

    [Fact]
    public void ScriptIdentityAndCheckpointNormalizeStableValues()
    {
        var id = new ScriptId(" chapter ", " Start ");
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            " fingerprint ",
            new ScriptPlaybackSpot(id, " line:intro "),
            ["Maya", "7", "True"]);

        Assert.Equal("chapter/Start", id.ToString());
        Assert.Equal("fingerprint", checkpoint.ProgramFingerprint);
        Assert.Equal("line:intro", checkpoint.Spot.LineId);
        Assert.Equal(["Maya", "7", "True"], checkpoint.ResolvedSubstitutions);
    }

    [Fact]
    public void ScriptDescriptorRejectsDuplicateStableLineIds()
    {
        var exception = Assert.Throws<ScriptCheckpointException>(() =>
            new ScriptDescriptor(
                new ScriptId("chapter", "Start"),
                "fingerprint",
                ["line:duplicate", "line:duplicate"]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptStateAutoAndHistoryPublicContractsDoNotExposeBackendTypes()
    {
        var stableNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            typeof(IScriptPlayback).Namespace!,
            typeof(IStateManager).Namespace!,
            typeof(IAutoManager).Namespace!,
            typeof(IHistoryManager).Namespace!,
        };
        var types = typeof(IScriptPlayback).Assembly.GetExportedTypes()
            .Where(type => type.Namespace is not null &&
                stableNamespaces.Contains(type.Namespace));

        foreach (var type in types)
        {
            foreach (var memberType in PublicMemberTypes(type).SelectMany(Expand))
            {
                Assert.False(
                    memberType.Namespace?.StartsWith("Yarn", StringComparison.Ordinal) == true ||
                    memberType.Namespace?.StartsWith("Godot", StringComparison.Ordinal) == true,
                    $"Public member on {type.FullName} exposes backend type " +
                    $"{memberType.FullName}.");
            }
        }
    }

    private static IEnumerable<Type> PublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

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

    private static IEnumerable<Type> Expand(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument))
            {
                yield return nested;
            }
        }
    }

    private sealed class FakeCheckpointBackend : IScriptCheckpointBackend
    {
        private readonly List<string> order;

        public FakeCheckpointBackend(List<string> order)
        {
            this.order = order;
        }

        public Exception? ValidationFailure { get; init; }

        public ScriptCheckpoint CaptureCheckpoint() => throw new NotSupportedException();

        public Task ValidateCheckpointAsync(
            ScriptCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            order.Add("validate");
            return ValidationFailure is null
                ? Task.CompletedTask
                : Task.FromException(ValidationFailure);
        }

        public void PrepareRestore(ScriptCheckpoint checkpoint) => order.Add("prepare");

        public void CancelPreparedRestore(ScriptCheckpoint checkpoint) => order.Add("cancel");
    }

    private sealed class FakeDialoguePlayer : IDialoguePlayer
    {
        private readonly List<string> order;

        public FakeDialoguePlayer(List<string> order)
        {
            this.order = order;
        }

        public event Action<DialogueStateChanged>? StateChanged { add { } remove { } }
        public event Action<DialogueStartRequest>? Started { add { } remove { } }
        public event Action<DialogueLine>? LinePresented { add { } remove { } }
        public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented { add { } remove { } }
        public event Action<DialogueCompletion>? Completed { add { } remove { } }
        public event Action<DialogueCompletion>? Stopped { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public DialogueSessionState State => DialogueSessionState.Idle;
        public string? CurrentProjectId => null;
        public string? CurrentNode => null;
        public string? CurrentLineId => null;
        public bool IsPresentingOptions => false;
        public IReadOnlyList<DialogueOption> CurrentOptions => Array.Empty<DialogueOption>();
        public Task<DialogueCompletion> Completion => Task.FromResult(
            new DialogueCompletion(DialogueCompletionKind.Completed, "project", "Start"));

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(
            DialogueStartRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            order.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            order.Add("stop");
            return Task.CompletedTask;
        }

        public void RequestHurryUp()
        {
        }

        public void RequestNextLine()
        {
        }
    }

    private sealed class FakeDialoguePresentation : IDialoguePresentation
    {
        public event Action? LineDisplayCompleted { add { } remove { } }
        public event Action? LineDismissed { add { } remove { } }
        public bool IsLineActive => false;
        public bool IsLineDisplayComplete => false;
        public bool IsPresentingOptions => false;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

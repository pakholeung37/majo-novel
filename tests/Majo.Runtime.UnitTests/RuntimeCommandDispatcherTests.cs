#nullable enable

using Majo.Runtime.Dialogue.Commands;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeCommandDispatcherTests
{
    private sealed class TestCommand : RuntimeCommand<TestCommand.Arguments>
    {
        private readonly Action onExecute;

        public TestCommand(Action onExecute)
        {
            this.onExecute = onExecute;
        }

        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("test_cmd").Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            onExecute();
            return ValueTask.CompletedTask;
        }

        public sealed record Arguments;
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_ExecutesAndQuiesces()
    {
        var dispatcher = new RuntimeCommandDispatcher();
        var executed = false;

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Add("test_cmd", _ => new TestCommand(() => executed = true), "test");
        cmdRegistry.SealRegistrations();
        var catalog = cmdRegistry.BuildCatalog();

        await dispatcher.ExecuteAsync(
            catalog.Commands[0],
            new CommandInput("test_cmd", Array.Empty<string>()),
            CancellationToken.None);
        Assert.True(executed);
        Assert.False(dispatcher.HasActiveCommands);

        // Quiescence drain should complete immediately when no commands are active
        await dispatcher.WaitForQuiescenceAsync(CancellationToken.None);
    }

    private sealed class FailCommand : RuntimeCommand<FailCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("fail_cmd").Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            throw new InvalidOperationException("boom");
        }

        public sealed record Arguments;
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_Fault_IsCapturedAndReported()
    {
        var dispatcher = new RuntimeCommandDispatcher(abortOnRecoverableFailure: true);

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Add("fail_cmd", _ => new FailCommand(), "test");
        cmdRegistry.SealRegistrations();
        var catalog = cmdRegistry.BuildCatalog();

        await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            dispatcher.ExecuteAsync(
                catalog.Commands[0],
                new CommandInput("fail_cmd", Array.Empty<string>()),
                CancellationToken.None));

        Assert.NotNull(dispatcher.LastFailure);
        Assert.False(dispatcher.HasActiveCommands);
    }

    private sealed class LongCommand : RuntimeCommand<LongCommand.Arguments>
    {
        private readonly TaskCompletionSource startedTcs;
        private readonly TaskCompletionSource cancelledTcs;

        public LongCommand(TaskCompletionSource startedTcs, TaskCompletionSource cancelledTcs)
        {
            this.startedTcs = startedTcs;
            this.cancelledTcs = cancelledTcs;
        }

        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("long_cmd").Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            startedTcs.SetResult();
            try
            {
                await Task.Delay(10000, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.SetResult();
                throw;
            }
        }

        public sealed record Arguments;
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueStop_CancelsActiveCommandsAndReachesQuiescence()
    {
        var dispatcher = new RuntimeCommandDispatcher();
        var startedTcs = new TaskCompletionSource();
        var cancelledTcs = new TaskCompletionSource();

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Add("long_cmd", _ => new LongCommand(startedTcs, cancelledTcs), "test");
        cmdRegistry.SealRegistrations();
        var catalog = cmdRegistry.BuildCatalog();

        using var cts = new CancellationTokenSource();
        var runTask = dispatcher.ExecuteAsync(
            catalog.Commands[0],
            new CommandInput("long_cmd", Array.Empty<string>()),
            cts.Token);

        await startedTcs.Task;
        Assert.True(dispatcher.HasActiveCommands);

        cts.Cancel();
        await runTask;
        await cancelledTcs.Task;
        Assert.False(dispatcher.HasActiveCommands);
    }
}

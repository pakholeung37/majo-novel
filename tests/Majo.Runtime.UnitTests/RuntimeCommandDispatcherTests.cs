#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeCommandDispatcherTests
{
    [RuntimeCommand("test_cmd")]
    private sealed class TestCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_ExecutesAndQuiesces()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder);
        var executed = false;

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<TestCmdArgs>((_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        });
        var catalog = cmdRegistry.BuildCatalog();

        await dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), CancellationToken.None);
        Assert.True(executed);
        Assert.False(dispatcher.HasActiveCommands);

        // Quiescence drain should complete immediately when no commands are active
        await dispatcher.WaitForQuiescenceAsync(CancellationToken.None);
    }

    [RuntimeCommand("fail_cmd")]
    private sealed class FailCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_Fault_IsCapturedAndReported()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder, abortOnRecoverableFailure: true);

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<FailCmdArgs>((_, _) => throw new InvalidOperationException("boom"));
        var catalog = cmdRegistry.BuildCatalog();

        await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), CancellationToken.None));

        Assert.NotNull(dispatcher.LastFailure);
        Assert.False(dispatcher.HasActiveCommands);
    }

    [RuntimeCommand("long_cmd")]
    private sealed class LongCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueStop_CancelsActiveCommandsAndReachesQuiescence()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder);
        var startedTcs = new TaskCompletionSource();
        var cancelledTcs = new TaskCompletionSource();

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<LongCmdArgs>(async (_, ctx) =>
        {
            startedTcs.SetResult();
            try
            {
                await Task.Delay(10000, ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.SetResult();
                throw;
            }
        });
        var catalog = cmdRegistry.BuildCatalog();

        using var cts = new CancellationTokenSource();
        var runTask = dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), cts.Token);

        await startedTcs.Task;
        Assert.True(dispatcher.HasActiveCommands);

        cts.Cancel();
        await runTask;
        await cancelledTcs.Task;

        await dispatcher.WaitForQuiescenceAsync(CancellationToken.None);
        Assert.False(dispatcher.HasActiveCommands);
    }
}

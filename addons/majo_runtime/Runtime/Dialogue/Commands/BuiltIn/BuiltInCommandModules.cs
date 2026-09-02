#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public static class BuiltInRuntimeCommands
{
    public static RuntimeCommandRegistry Register(
        RuntimeCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands.RegisterModule(new PlaybackCommandModule());
    }
}

public sealed class PlaybackCommandModule : IRuntimeCommandModule
{
    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new AwaitCommandHandler())
            .Register(new EndAwaitCommandHandler())
            .Register(new CancelCommandHandler())
            .Register(new SaveCommandHandler())
            .Register(new AutoSaveCommandHandler());
    }
}

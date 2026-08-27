#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public static class BuiltInRuntimeCommands
{
    public static RuntimeCommandRegistry Register(
        RuntimeCommandRegistry commands,
        IActorCommandService actorCommands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(actorCommands);
        return commands.RegisterModule(new ActorCommandModule(actorCommands))
            .RegisterModule(new PlaybackCommandModule());
    }
}

public sealed class ActorCommandModule : IRuntimeCommandModule
{
    private readonly IActorCommandService actorCommands;

    public ActorCommandModule(IActorCommandService actorCommands)
    {
        this.actorCommands = actorCommands ?? throw new ArgumentNullException(nameof(actorCommands));
    }

    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new BackCommandHandler(actorCommands))
            .Register(new CharacterCommandHandler(actorCommands))
            .Register(new HideCommandHandler(actorCommands));
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

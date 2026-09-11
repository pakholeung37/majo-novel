#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Demos.Live2D.Actors;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Demos.Live2D;

[Command("live2d motion", Category = "Live2D")]
public sealed partial class Live2DMotionCommand : RuntimeCommand<Live2DMotionCommand.Arguments>
{
    private readonly Live2DActorRegistry actors;

    public Live2DMotionCommand(Live2DActorRegistry actors)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    protected override async ValueTask ExecuteAsync(
        Arguments arguments,
        CommandExecutionContext context)
    {
        await actors.PlayMotionAsync(
            arguments.Actor,
            arguments.Motion,
            arguments.Wait.GetValueOrDefault(false),
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Actor,
        [Option] string Motion,
        [Option(DefaultText = "false")] CommandValue<bool> Wait);
}

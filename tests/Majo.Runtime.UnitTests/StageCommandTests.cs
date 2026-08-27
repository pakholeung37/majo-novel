using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Stage;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StageCommandTests
{
    [Fact]
    public void StageCommandModuleBuildsAValidatedCatalog()
    {
        var registry = new RuntimeCommandRegistry();
        registry.RegisterModule(new StageCommandModule());
        var catalog = registry.BuildCatalog();

        Assert.Contains(catalog.Commands, command => command.Name == "cameraMove");
        Assert.Contains(catalog.Commands, command => command.Name == "transition");
        Assert.Contains(catalog.Commands, command => command.Name == "curtain");
        Assert.Contains(catalog.Commands, command => command.Name == "flash");

        var binder = new CommandBinder(new CommandBindingContext(0.35));
        foreach (var command in catalog.Commands) binder.Validate(command.Descriptor);

        var move = catalog.Get("cameraMove");
        var arguments = binder.Bind<CameraMoveCommandArguments>(
            move.Descriptor, new[] { "12", "y:-4", "wait:true" });
        Assert.Equal(12, arguments.X);
        Assert.Equal(-4, arguments.Y);
        Assert.True(arguments.Wait);

        var curtain = catalog.Get("curtain");
        var curtainArguments = binder.Bind<StageCurtainCommandArguments>(
            curtain.Descriptor, new[] { "closed", "duration:1.2", "wait:true" });
        Assert.Equal(StageCurtainPosition.Closed, curtainArguments.Position);
        Assert.Equal(1.2, curtainArguments.Duration);
        Assert.True(curtainArguments.Wait);
    }
}

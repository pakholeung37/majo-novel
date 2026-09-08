using System.Text.Json;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionCompletionTests
{
    [Fact]
    public void GeneratedCompletionMatchesTheRuntimeCatalog()
    {
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        var beats = new DirectionBeatRuntime(direction);
        var services = new ServiceResolverBuilder()
            .Add<IDirectionTransactionCoordinator>(direction)
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDirectionScopeResolver>(new DirectionScopeResolver())
            .Add<IDirectionCommandPolicy>(new DirectionCommandPolicy(beats))
            .Add<IDialogueNodeAccessor>(new DelegateDialogueNodeAccessor(() => "node"))
            .Build();
        var registry = new RuntimeCommandRegistry();
        registry.RegisterModule(new DirectionCommandModule());
        var catalog = registry.BuildCatalog(services);
        foreach (var removed in new[] { "camera pose", "leave", "stage release", "view release" })
            Assert.False(catalog.TryGet(removed, out _));
        Assert.Throws<RuntimeCommandException>(() => catalog.Get("camera").BindUntyped(new CommandInput("camera", new[] { "pose", "zoom:2" })));
        Assert.NotNull(catalog.Get("character").BindUntyped(new CommandInput("character", new[] { "hero" })));
        Assert.NotNull(catalog.Get("object fx").BindUntyped(new CommandInput("object fx", new[] { "knife", "effect:shake" })));
        Assert.NotNull(catalog.Get("backdrop fx").BindUntyped(new CommandInput("backdrop fx", new[] { "shake" })));
        var payload = new
        {
            version = 2,
            commands = catalog.Commands.Select(command => new
            {
                yarnName = command.Name,
                definitionName = command.Command.GetType().FullName,
                language = "csharp",
                documentation = command.Usage + "\n" + string.Join("\n", command.Slots.Select(slot =>
                    slot.ScriptName + ": " + slot.Declaration.ValueHint +
                    (slot.Declaration.DefaultText is { } value ? " (default " + value + ")" : ""))),
                async = true,
                containsErrors = false,
                parameters = command.Slots.Select(slot => new
                {
                    name = slot.Declaration.Primary ? slot.ScriptName : slot.ScriptName + ":value",
                    type = "string",
                    isOptional = !slot.Declaration.Required,
                    isParamsArray = false,
                }).ToArray(),
            }).ToArray(),
            functions = Array.Empty<object>(),
        };
        var generated = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Majo Rebuild.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var output = Path.Combine(root.FullName, "Majo.Direction.ysls.json");
        if (Environment.GetEnvironmentVariable("MAJO_UPDATE_COMPLETIONS") == "1") File.WriteAllText(output, generated);
        Assert.Equal(generated, File.ReadAllText(output));
    }
}

#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Save;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class SaveCommand : RuntimeCommand<SaveCommand.Arguments>
{
    private readonly ISaveProductAccessor? saveAccessor;

    public SaveCommand(ISaveProductAccessor? saveAccessor)
    {
        this.saveAccessor = saveAccessor;
    }

    public SaveCommand(ISaveProductManager? saveProduct)
        : this(saveProduct is not null ? new DelegateSaveProductAccessor(() => saveProduct) : null)
    {
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("save", category: "Save", usage: "save")
            .Build(static _ => new Arguments());

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var saveProduct = saveAccessor?.SaveProduct;
        if (saveProduct is null)
        {
            throw new RuntimeCommandException(
                "save",
                "Save support is disabled or unavailable.");
        }

        saveProduct.RequestQuickSaveCandidate("command:save");
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments;
}

public sealed class AutoSaveCommand : RuntimeCommand<AutoSaveCommand.Arguments>
{
    private readonly ISaveProductAccessor? saveAccessor;

    public AutoSaveCommand(ISaveProductAccessor? saveAccessor)
    {
        this.saveAccessor = saveAccessor;
    }

    public AutoSaveCommand(ISaveProductManager? saveProduct)
        : this(saveProduct is not null ? new DelegateSaveProductAccessor(() => saveProduct) : null)
    {
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("autoSave", category: "Save", usage: "autoSave")
            .Build(static _ => new Arguments());

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var saveProduct = saveAccessor?.SaveProduct;
        if (saveProduct is null)
        {
            throw new RuntimeCommandException(
                "autoSave",
                "Save support is disabled or unavailable.");
        }

        saveProduct.RequestAutoSaveCandidate("command:autoSave");
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments;
}

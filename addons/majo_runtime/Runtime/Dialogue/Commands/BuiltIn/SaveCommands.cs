#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Save;

namespace Majo.Runtime.Dialogue.Commands;

[Command("save", Category = "Save")]
public sealed partial class SaveCommand : RuntimeCommand<SaveCommand.Arguments>
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

[Command("autoSave", Category = "Save")]
public sealed partial class AutoSaveCommand : RuntimeCommand<AutoSaveCommand.Arguments>
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

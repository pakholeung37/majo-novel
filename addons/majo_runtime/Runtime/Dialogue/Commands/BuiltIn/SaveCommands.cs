#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Save;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand("save", Category = "Save", Usage = "save")]
public sealed class SaveCommandArguments
{
}

public sealed class SaveCommandHandler : IRuntimeCommandHandler<SaveCommandArguments>
{
    public Task ExecuteAsync(SaveCommandArguments arguments, RuntimeCommandContext context)
    {
        RequireSaveProduct(context, "save").RequestQuickSaveCandidate("command:save");
        return Task.CompletedTask;
    }

    internal static ISaveProductManager RequireSaveProduct(
        RuntimeCommandContext context,
        string commandName)
    {
        if (context.Services?.TryGet<ISaveProductManager>(out var saveProduct) == true &&
            saveProduct is not null)
        {
            return saveProduct;
        }

        throw new RuntimeCommandException(
            commandName,
            "Save support is disabled or unavailable.");
    }
}

[RuntimeCommand("autoSave", Category = "Save", Usage = "autoSave")]
public sealed class AutoSaveCommandArguments
{
}

public sealed class AutoSaveCommandHandler : IRuntimeCommandHandler<AutoSaveCommandArguments>
{
    public Task ExecuteAsync(AutoSaveCommandArguments arguments, RuntimeCommandContext context)
    {
        SaveCommandHandler.RequireSaveProduct(context, "autoSave")
            .RequestAutoSaveCandidate("command:autoSave");
        return Task.CompletedTask;
    }
}

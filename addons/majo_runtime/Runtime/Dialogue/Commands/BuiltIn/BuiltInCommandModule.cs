#nullable enable

using System;
using Majo.Runtime.Save;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class BuiltInCommandModule : IRuntimeCommandModule
{
    public void Register(CommandRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RegisterCommands(context.Commands);
    }

    public static void RegisterCommands(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        const string providerId = "majo.builtin";
        const string version = "1.0";

        commands.Add(
            "save",
            services => new SaveCommand(
                services.TryGet<ISaveProductAccessor>(out var accessor)
                    ? accessor
                    : services.TryGet<ISaveProductManager>(out var manager)
                        ? new DelegateSaveProductAccessor(() => manager)
                        : null),
            providerId,
            version);
        commands.Add(
            "autoSave",
            services => new AutoSaveCommand(
                services.TryGet<ISaveProductAccessor>(out var accessor)
                    ? accessor
                    : services.TryGet<ISaveProductManager>(out var manager)
                        ? new DelegateSaveProductAccessor(() => manager)
                        : null),
            providerId,
            version);
    }
}

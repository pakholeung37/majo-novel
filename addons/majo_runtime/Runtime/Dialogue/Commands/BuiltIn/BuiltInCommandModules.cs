#nullable enable

using System;
using Majo.Runtime.Save;

namespace Majo.Runtime.Dialogue.Commands;

public static class BuiltInRuntimeCommands
{
    public static void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Add(
            "save",
            services => new SaveCommand(
                services.TryGet<ISaveProductAccessor>(out var accessor)
                    ? accessor
                    : services.TryGet<ISaveProductManager>(out var manager)
                        ? new DelegateSaveProductAccessor(() => manager)
                        : null),
            "majo.builtin",
            "1.0");
        commands.Add(
            "autoSave",
            services => new AutoSaveCommand(
                services.TryGet<ISaveProductAccessor>(out var accessor)
                    ? accessor
                    : services.TryGet<ISaveProductManager>(out var manager)
                        ? new DelegateSaveProductAccessor(() => manager)
                        : null),
            "majo.builtin",
            "1.0");
    }
}

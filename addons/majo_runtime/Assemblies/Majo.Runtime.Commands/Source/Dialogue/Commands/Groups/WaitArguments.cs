#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class BlockingWait : ICommandGroupConfiguration
{
    public void Configure(CommandGroupConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetDefaultText("Wait", "true");
    }
}

public sealed class NonBlockingWait : ICommandGroupConfiguration
{
    public void Configure(CommandGroupConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetDefaultText("Wait", "false");
    }
}

public sealed record WaitArguments(
    [Option(ValueHint = "bool", DefaultText = "false", Order = 20)]
    CommandValue<bool> Wait);

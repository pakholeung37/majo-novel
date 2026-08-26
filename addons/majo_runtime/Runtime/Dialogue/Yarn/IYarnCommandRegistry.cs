#nullable enable

using System;
using System.Threading;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandRegistry
{
    event Action<string>? UnhandledCommand
    {
        add { }
        remove { }
    }

    CancellationToken DialogueCancellationToken => CancellationToken.None;

    void AddCommandHandler(string commandName, Delegate handler);

    void RemoveCommandHandler(string commandName);

    void CancelDialogue()
    {
    }

    void AbortDialogue()
    {
    }

    void Dispose()
    {
    }
}

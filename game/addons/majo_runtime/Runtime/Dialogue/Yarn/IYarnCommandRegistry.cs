#nullable enable

using System;
using System.Threading;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandRegistry
{
    CancellationToken DialogueCancellationToken => CancellationToken.None;

    void AddCommandHandler(string commandName, Delegate handler);

    void RemoveCommandHandler(string commandName);

    void AbortDialogue()
    {
    }

    void Dispose()
    {
    }
}

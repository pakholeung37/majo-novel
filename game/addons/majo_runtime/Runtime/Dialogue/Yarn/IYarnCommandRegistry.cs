#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandRegistry
{
    void AddCommandHandler(string commandName, Delegate handler);

    void RemoveCommandHandler(string commandName);
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandAdapter : IRuntimeService
{
    event System.Action<RuntimeCommandException>? CommandFailed;

    RuntimeCommandException? LastFailure { get; }

    bool HasActiveCommands { get; }

    IReadOnlyList<RuntimeCommandActivity> ActiveCommands =>
        Array.Empty<RuntimeCommandActivity>();

    void CancelDialogue();

    Task DrainDialogueOperationsAsync(CancellationToken cancellationToken);
}

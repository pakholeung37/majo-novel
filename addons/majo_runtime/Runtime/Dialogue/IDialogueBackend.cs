#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue;

public interface IDialogueBackend : IRuntimeService
{
    event Action<string>? NodeEntered;

    event Action<string>? NodeExited;

    event Action<DialogueLine>? LinePresented;

    event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

    event Action<Exception>? Failed;

    Task<DialogueCompletion> Completion { get; }

    Task StartAsync(
        string projectId,
        string startNode,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    void RequestHurryUp();

    void RequestNextLine();
}

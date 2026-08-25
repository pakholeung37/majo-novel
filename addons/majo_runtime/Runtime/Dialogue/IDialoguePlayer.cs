#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue;

public interface IDialoguePlayer : IRuntimeService
{
    event Action<DialogueStateChanged>? StateChanged;

    event Action<DialogueStartRequest>? Started;

    event Action<DialogueLine>? LinePresented;

    event Action<IReadOnlyList<DialogueOption>>? OptionsPresented;

    event Action<DialogueCompletion>? Completed;

    event Action<DialogueCompletion>? Stopped;

    event Action<Exception>? Failed;

    DialogueSessionState State { get; }

    string? CurrentProjectId { get; }

    string? CurrentNode { get; }

    string? CurrentLineId { get; }

    bool IsPresentingOptions { get; }

    IReadOnlyList<DialogueOption> CurrentOptions { get; }

    Task<DialogueCompletion> Completion { get; }

    Task StartAsync(
        DialogueStartRequest? request = null,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    void RequestHurryUp();

    void RequestNextLine();
}

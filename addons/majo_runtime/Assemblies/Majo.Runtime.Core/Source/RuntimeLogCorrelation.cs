#nullable enable

using System;

namespace Majo.Runtime.Core;

public readonly record struct RuntimeLogCorrelation
{
    public string RuntimeId { get; init; }
    public string? SessionId { get; init; }
    public string? DialogueSessionId { get; init; }
    public string? CommandId { get; init; }

    public RuntimeLogCorrelation(
        string runtimeId,
        string? sessionId = null,
        string? dialogueSessionId = null,
        string? commandId = null)
    {
        RuntimeId = runtimeId;
        SessionId = sessionId;
        DialogueSessionId = dialogueSessionId;
        CommandId = commandId;
    }

    public static RuntimeLogCorrelation Empty { get; } = new(string.Empty);

    public static RuntimeLogCorrelation CreateRoot(string? runtimeId = null) =>
        new(runtimeId ?? Guid.NewGuid().ToString("N"));

    public RuntimeLogCorrelation WithSessionId(string? sessionId) =>
        this with { SessionId = sessionId };

    public RuntimeLogCorrelation WithDialogueSessionId(string? dialogueSessionId) =>
        this with { DialogueSessionId = dialogueSessionId };

    public RuntimeLogCorrelation WithCommandId(string? commandId) =>
        this with { CommandId = commandId };

}

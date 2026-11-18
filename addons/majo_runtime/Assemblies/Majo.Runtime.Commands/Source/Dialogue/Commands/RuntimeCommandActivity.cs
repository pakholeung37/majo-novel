#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record RuntimeCommandActivity(
    string CommandName,
    string ExecutionId,
    string? SourceLineId,
    DateTimeOffset StartedAt);

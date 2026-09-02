#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue;

public delegate string? DialogueSpeakerNameResolver(string speakerId, string locale);

public enum DialogueSessionState
{
    Idle,
    Loading,
    Running,
    Stopping,
    Faulted,
}

public enum DialogueCompletionKind
{
    Completed,
    Stopped,
}

public sealed record DialogueStartRequest(
    string? ProjectId = null,
    string? StartNode = null);

public sealed record DialogueCompletion(
    DialogueCompletionKind Kind,
    string ProjectId,
    string StartNode);

public sealed record DialogueStateChanged(
    DialogueSessionState Previous,
    DialogueSessionState Current);

public enum DialogueMarkupValueKind
{
    WholeNumber,
    Number,
    Text,
    Boolean,
}

public readonly record struct DialogueMarkupValue
{
    private DialogueMarkupValue(
        DialogueMarkupValueKind kind,
        int integerValue,
        double numberValue,
        string? stringValue,
        bool booleanValue)
    {
        Kind = kind;
        IntegerValue = integerValue;
        NumberValue = numberValue;
        StringValue = stringValue;
        BooleanValue = booleanValue;
    }

    public DialogueMarkupValueKind Kind { get; }

    public int IntegerValue { get; }

    public double NumberValue { get; }

    public string? StringValue { get; }

    public bool BooleanValue { get; }

    public static DialogueMarkupValue FromInteger(int value) => new(
        DialogueMarkupValueKind.WholeNumber,
        value,
        default,
        default,
        default);

    public static DialogueMarkupValue FromNumber(double value) => new(
        DialogueMarkupValueKind.Number,
        default,
        value,
        default,
        default);

    public static DialogueMarkupValue FromString(string value) => new(
        DialogueMarkupValueKind.Text,
        default,
        default,
        value ?? throw new ArgumentNullException(nameof(value)),
        default);

    public static DialogueMarkupValue FromBoolean(bool value) => new(
        DialogueMarkupValueKind.Boolean,
        default,
        default,
        default,
        value);
}

public sealed record DialogueMarkupSpan(
    string Name,
    int Position,
    int Length,
    IReadOnlyDictionary<string, DialogueMarkupValue> Properties);

public sealed record DialogueText(
    string Text,
    IReadOnlyList<DialogueMarkupSpan> Attributes);

public enum DialoguePresentationOrigin
{
    Normal,
    CheckpointRestore,
    HotReloadReplay,
}

public sealed record DialogueLine(
    string Id,
    string? Speaker,
    DialogueText Text,
    IReadOnlyList<string> Metadata,
    string? ResolvedLocale = null,
    DialoguePresentationOrigin Origin = DialoguePresentationOrigin.Normal);

public sealed record DialogueOption(
    int Id,
    DialogueLine Line,
    bool IsAvailable,
    IReadOnlyList<string>? Substitutions = null);

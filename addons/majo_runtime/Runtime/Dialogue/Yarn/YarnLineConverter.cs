#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Yarn.Markup;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;
using YarnLocalizedLine = YarnSpinnerGodot.LocalizedLine;

namespace Majo.Runtime.Dialogue.Yarn;

public static class YarnLineConverter
{
    public static DialogueText Convert(MarkupParseResult text)
    {
        return new DialogueText(
            text.Text,
            text.Attributes.Select(Convert).ToArray());
    }

    public static DialogueLine Convert(
        YarnLocalizedLine line,
        string? resolvedLocale = null,
        DialogueSpeakerNameResolver? speakerNameResolver = null,
        string? speakerLocale = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.TextWithoutCharacterName;
        var speaker = line.CharacterName;
        if (!string.IsNullOrWhiteSpace(speaker) && speakerNameResolver is not null)
        {
            speaker = speakerNameResolver(
                speaker,
                speakerLocale ?? resolvedLocale ?? string.Empty) ?? speaker;
        }

        return new DialogueLine(
            line.TextID,
            speaker,
            Convert(text),
            (line.Metadata ?? Array.Empty<string>()).ToArray(),
            resolvedLocale);
    }

    public static DialogueOption Convert(YarnDialogueOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return new DialogueOption(
            option.DialogueOptionID,
            Convert(option.Line),
            option.IsAvailable,
            Array.AsReadOnly(option.Line.Substitutions.ToArray()));
    }

    private static DialogueMarkupSpan Convert(MarkupAttribute attribute)
    {
        var properties = attribute.Properties.ToDictionary(
            property => property.Key,
            property => Convert(property.Value),
            StringComparer.Ordinal);
        return new DialogueMarkupSpan(
            attribute.Name,
            attribute.Position,
            attribute.Length,
            new ReadOnlyDictionary<string, DialogueMarkupValue>(properties));
    }

    private static DialogueMarkupValue Convert(MarkupValue value) => value.Type switch
    {
        MarkupValueType.Integer => DialogueMarkupValue.FromInteger(value.IntegerValue),
        MarkupValueType.Float => DialogueMarkupValue.FromNumber(value.FloatValue),
        MarkupValueType.String => DialogueMarkupValue.FromString(value.StringValue),
        MarkupValueType.Bool => DialogueMarkupValue.FromBoolean(value.BoolValue),
        _ => throw new DialogueException($"Unsupported Yarn markup value type '{value.Type}'."),
    };
}

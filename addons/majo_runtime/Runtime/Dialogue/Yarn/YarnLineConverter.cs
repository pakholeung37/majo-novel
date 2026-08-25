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
        ArgumentNullException.ThrowIfNull(text);
        return new DialogueText(
            text.Text,
            text.Attributes.Select(Convert).ToArray());
    }

    public static DialogueLine Convert(YarnLocalizedLine line, string? resolvedLocale = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.TextWithoutCharacterName;
        return new DialogueLine(
            line.TextID,
            line.CharacterName,
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

    private static DialogueMarkupAttribute Convert(MarkupAttribute attribute)
    {
        var properties = attribute.Properties.ToDictionary(
            property => property.Key,
            property => Convert(property.Value),
            StringComparer.Ordinal);
        return new DialogueMarkupAttribute(
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

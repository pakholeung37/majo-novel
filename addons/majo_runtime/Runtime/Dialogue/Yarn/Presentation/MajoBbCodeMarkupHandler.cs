#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Godot;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed class MajoBbCodeMarkupHandler : IActionMarkupHandler
{
    private static readonly IReadOnlyDictionary<string, (string Open, string Close)> Styles =
        new Dictionary<string, (string Open, string Close)>(StringComparer.Ordinal)
        {
            ["b"] = ("[b]", "[/b]"),
            ["i"] = ("[i]", "[/i]"),
            ["u"] = ("[u]", "[/u]"),
            ["s"] = ("[s]", "[/s]"),
        };

    public void OnPrepareForLine(MarkupParseResult line, RichTextLabel text)
    {
    }

    public void OnLineDisplayBegin(MarkupParseResult line, RichTextLabel text)
    {
        text.Text = ApplyStyles(line);
    }

    public YarnTask OnCharacterWillAppear(
        int currentCharacterIndex,
        MarkupParseResult line,
        CancellationToken cancellationToken) => YarnTask.CompletedTask;

    public void OnLineDisplayComplete()
    {
    }

    public void OnLineWillDismiss()
    {
    }

    private static string ApplyStyles(MarkupParseResult line)
    {
        var textElementOffsets = System.Globalization.StringInfo.ParseCombiningCharacters(
            line.Text);
        var boundaries = new List<StyledBoundary>[textElementOffsets.Length + 1];
        var sourceOrder = 0;
        foreach (var attribute in line.Attributes)
        {
            if (!Styles.TryGetValue(attribute.Name, out var style) ||
                attribute.Position < 0 ||
                attribute.Length <= 0 ||
                attribute.Position + attribute.Length > textElementOffsets.Length)
            {
                continue;
            }

            var boundary = new StyledBoundary(
                attribute.Position,
                sourceOrder++,
                style.Open,
                style.Close);
            (boundaries[attribute.Position] ??= new()).Add(boundary);
            (boundaries[attribute.Position + attribute.Length] ??= new()).Add(boundary);
        }

        var result = new StringBuilder(line.Text.Length + 32);
        for (var index = 0; index <= textElementOffsets.Length; index++)
        {
            var styles = boundaries[index];
            if (styles is not null)
            {
                foreach (var style in styles
                    .FindAll(item => item.Start < index)
                    .OrderByDescending(item => item.SourceOrder))
                {
                    result.Append(style.Close);
                }

                foreach (var style in styles
                    .FindAll(item => item.Start == index)
                    .OrderBy(item => item.SourceOrder))
                {
                    result.Append(style.Open);
                }
            }

            if (index == textElementOffsets.Length)
            {
                continue;
            }

            var offset = textElementOffsets[index];
            var nextOffset = index + 1 < textElementOffsets.Length
                ? textElementOffsets[index + 1]
                : line.Text.Length;
            result.Append(line.Text, offset, nextOffset - offset);
        }

        return result.ToString();
    }

    private sealed record StyledBoundary(
        int Start,
        int SourceOrder,
        string Open,
        string Close);
}

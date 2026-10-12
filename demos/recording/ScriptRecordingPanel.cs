#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Godot;

namespace Majo.Demos.Recording;

public partial class ScriptRecordingPanel : Control
{
    private sealed record RowSegment(string Text, float X, Color Color);
    private sealed record WrappedRow(int Line, bool First, IReadOnlyList<RowSegment> Segments);

    private readonly Font font = LoadFont();
    private readonly List<WrappedRow> rows = new();
    private string file = string.Empty;
    private string node = string.Empty;
    private string[] lines = [];
    private int activeLine;
    private int targetRow;
    private float scrollRow;
    private static readonly Regex InternalIds = new(@"\s+#(?:line|voice):\S+", RegexOptions.CultureInvariant);

    public const int VisibleRows = 10;
    private const int FontSize = 36;
    private const int RowHeight = 54;
    private const float HeaderBottom = 54f;
    public const float PanelHeight = HeaderBottom + VisibleRows * RowHeight + 16f;
    private const float CodeX = 24f;
    private const float PaddingRight = 24f;

    private static readonly Color CommentColor = new("687685");
    private static readonly Color DelimiterColor = new("b2bdcc");
    private static readonly Color HeaderKeyColor = new("8fa6bd");
    private static readonly Color HeaderValueColor = new("bcc8d4");
    private static readonly Color CommandColor = new("8bd5ff");
    private static readonly Color SubcommandColor = new("d8b4fe");
    private static readonly Color ControlKeywordColor = new("f0abfc");
    private static readonly Color ParamKeyColor = new("93c5fd");
    private static readonly Color ParamValueColor = new("bbf7d0");
    private static readonly Color NumberColor = new("fdba74");
    private static readonly Color StringColor = new("99f6e4");
    private static readonly Color VariableColor = new("d8b4fe");
    private static readonly Color SpeakerColor = new("f6d98b");
    private static readonly Color DialogueColor = new("f0f4f8");
    private static readonly Color MarkupColor = new("a5b4fc");
    private static readonly Color LineTagColor = new("566370");
    private static readonly Color ActiveTextColor = new("65ffe4");
    private static readonly Color AccentColor = new("25f4cc");

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elseif", "endif", "declare", "set", "jump", "detour", "gosub", "and", "or", "not",
    };

    private static readonly HashSet<string> Subcommands = new(StringComparer.Ordinal)
    {
        "move", "preset", "define", "show", "hide", "fx", "motion", "reset", "begin", "end", "open", "closed",
    };

    private static Font LoadFont()
    {
        Font result = (Font)ThemeDB.FallbackFont.Duplicate();
        const string menlo = "/System/Library/Fonts/Menlo.ttc";
        if (File.Exists(menlo))
        {
            var mono = new FontFile();
            if (mono.LoadDynamicFont(menlo) == Error.Ok) result = mono;
        }
        var chinese = ResourceLoader.Load<Font>("res://demos/manosaba/assets/fonts/SourceHanSansKR.otf");
        if (chinese is not null) result.Fallbacks = [chinese];
        return result;
    }

    public override void _ExitTree()
    {
        font.Dispose();
    }

    public void ShowPosition(string path, string nodeName, int line, string source)
    {
        node = nodeName;
        if (file != path)
        {
            file = path;
            lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            targetRow = 0;
            scrollRow = 0;
            WrapLines();
        }
        activeLine = line;
        var currentRow = rows.FindIndex(row => row.Line == line);
        var maxTarget = Math.Max(0, rows.Count - VisibleRows);
        if (currentRow >= 0) targetRow = Math.Clamp(currentRow - (VisibleRows / 2 - 1), 0, maxTarget);
        QueueRedraw();
    }

    public override void _Ready()
    {
        ClipContents = true;
        Resized += WrapLines;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        if (Math.Abs(scrollRow - targetRow) < 0.01f)
        {
            if (scrollRow != targetRow)
            {
                scrollRow = targetRow;
                QueueRedraw();
            }
            return;
        }
        scrollRow = Mathf.Lerp(scrollRow, targetRow, (float)(1 - Math.Exp(-delta * 8)));
        QueueRedraw();
    }

    private void WrapLines()
    {
        rows.Clear();
        var maxWidth = Math.Max(120f, Size.X - CodeX - PaddingRight);
        var indentWidth = Measure("  ");
        for (var index = 0; index < lines.Length; index++)
        {
            var expanded = CleanLine(lines[index]).Replace("\t", "    ", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(expanded)) continue;
            var colors = HighlightLine(expanded);
            var offset = 0;
            var first = true;
            while (offset < expanded.Length)
            {
                var available = first ? maxWidth : Math.Max(80f, maxWidth - indentWidth);
                var length = 0;
                var measured = 0f;
                var lastBreak = -1;
                var inBracket = false;
                while (offset + length < expanded.Length)
                {
                    var ch = expanded[offset + length];
                    var charWidth = Measure(ch.ToString());
                    if (length > 0 && measured + charWidth > available) break;
                    if (ch == '[') inBracket = true;
                    else if (ch == ']') inBracket = false;
                    measured += charWidth;
                    length++;
                    if (!inBracket && length > 8 && (ch == ' ' || ch is '，' or '。' or '！' or '？' or '；' or ']'))
                        lastBreak = length;
                }
                if (offset + length < expanded.Length && lastBreak >= length / 2)
                    length = lastBreak;
                var segments = BuildSegments(expanded, colors, offset, length, first ? 0f : indentWidth);
                rows.Add(new WrappedRow(index + 1, first, segments));
                offset += length;
                while (offset < expanded.Length && expanded[offset] == ' ') offset++;
                first = false;
            }
        }
        QueueRedraw();
    }

    private static string CleanLine(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (inString && line[index] == '\\') { index++; continue; }
            if (line[index] == '"') inString = !inString;
            if (!inString && line[index] == '/' && index + 1 < line.Length && line[index + 1] == '/' &&
                (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                line = line[..index];
                break;
            }
        }
        return InternalIds.Replace(line, string.Empty).TrimEnd();
    }

    private List<RowSegment> BuildSegments(string line, Color[] colors, int start, int length, float initialX)
    {
        var result = new List<RowSegment>();
        if (length <= 0) return result;
        var x = initialX;
        var runStart = start;
        var runColor = colors[start];
        for (var i = start + 1; i < start + length; i++)
        {
            if (colors[i] == runColor) continue;
            var slice = line[runStart..i];
            result.Add(new RowSegment(slice, x, runColor));
            x += Measure(slice);
            runStart = i;
            runColor = colors[i];
        }
        var tail = line[runStart..(start + length)];
        result.Add(new RowSegment(tail, x, runColor));
        return result;
    }

    private float Measure(string text) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize).X;

    public static Color[] HighlightLine(string line)
    {
        var colors = new Color[line.Length];
        Array.Fill(colors, DialogueColor);
        if (line.Length == 0) return colors;

        var firstNonSpace = 0;
        while (firstNonSpace < line.Length && char.IsWhiteSpace(line[firstNonSpace])) firstNonSpace++;
        if (firstNonSpace >= line.Length) return colors;

        var trimmed = line.AsSpan(firstNonSpace);
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            Array.Fill(colors, CommentColor, firstNonSpace, line.Length - firstNonSpace);
            return colors;
        }

        if (trimmed.SequenceEqual("---") || trimmed.SequenceEqual("==="))
        {
            Array.Fill(colors, DelimiterColor, firstNonSpace, line.Length - firstNonSpace);
            return colors;
        }

        if (IsHeaderKey(trimmed, out var headerColon))
        {
            var colonIndex = firstNonSpace + headerColon;
            Array.Fill(colors, HeaderKeyColor, firstNonSpace, headerColon);
            colors[colonIndex] = DelimiterColor;
            if (colonIndex + 1 < line.Length)
                Array.Fill(colors, HeaderValueColor, colonIndex + 1, line.Length - colonIndex - 1);
            return colors;
        }

        if (trimmed.StartsWith("<<", StringComparison.Ordinal))
        {
            HighlightCommandSpan(line, colors, firstNonSpace);
            return colors;
        }

        var cursor = firstNonSpace;
        if (trimmed.StartsWith("->", StringComparison.Ordinal))
        {
            colors[cursor] = SpeakerColor;
            colors[cursor + 1] = SpeakerColor;
            cursor += 2;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;
        }

        var speakerColon = FindSpeakerColon(line, cursor);
        if (speakerColon > cursor)
        {
            Array.Fill(colors, SpeakerColor, cursor, speakerColon - cursor);
            colors[speakerColon] = DelimiterColor;
            cursor = speakerColon + 1;
        }

        HighlightInlineDialogue(line, colors, cursor);
        return colors;
    }

    private static bool IsHeaderKey(ReadOnlySpan<char> trimmed, out int colonOffset)
    {
        colonOffset = trimmed.IndexOf(':');
        if (colonOffset <= 0) return false;
        var key = trimmed[..colonOffset];
        return key.SequenceEqual("title") || key.SequenceEqual("tags") ||
               key.SequenceEqual("scene") || key.SequenceEqual("position") || key.SequenceEqual("color");
    }

    private static void HighlightCommandSpan(string line, Color[] colors, int start)
    {
        colors[start] = DelimiterColor;
        colors[start + 1] = DelimiterColor;
        var end = line.LastIndexOf(">>", StringComparison.Ordinal);
        var limit = end >= start + 2 ? end : line.Length;
        if (end >= start + 2)
        {
            colors[end] = DelimiterColor;
            colors[end + 1] = DelimiterColor;
            if (end + 2 < line.Length)
                HighlightInlineDialogue(line, colors, end + 2);
        }

        var index = start + 2;
        var tokenPosition = 0;
        var primaryCommand = string.Empty;
        while (index < limit)
        {
            while (index < limit && char.IsWhiteSpace(line[index])) index++;
            if (index >= limit) break;

            var tokenStart = index;
            while (index < limit && !char.IsWhiteSpace(line[index]))
            {
                if (line[index] == '"')
                {
                    index++;
                    while (index < limit && line[index] != '"') index++;
                }
                if (index < limit) index++;
            }

            var tokenLength = index - tokenStart;
            var token = line.Substring(tokenStart, tokenLength);
            if (tokenPosition == 0)
            {
                primaryCommand = token;
                var cmdColor = ControlKeywords.Contains(token) ? ControlKeywordColor : CommandColor;
                Array.Fill(colors, cmdColor, tokenStart, tokenLength);
            }
            else
            {
                var colon = token.IndexOf(':');
                if (colon > 0)
                {
                    Array.Fill(colors, ParamKeyColor, tokenStart, colon);
                    colors[tokenStart + colon] = DelimiterColor;
                    HighlightValueSpan(line, colors, tokenStart + colon + 1, tokenLength - colon - 1);
                }
                else if (tokenPosition == 1 && Subcommands.Contains(token))
                {
                    Array.Fill(colors, SubcommandColor, tokenStart, tokenLength);
                }
                else if (ControlKeywords.Contains(token))
                {
                    Array.Fill(colors, ControlKeywordColor, tokenStart, tokenLength);
                }
                else if (tokenPosition == 1 && primaryCommand is "character" or "live2d" or "backdrop" or "bgm" or "sfx" or "mark" or "object" or "stage" or "view")
                {
                    Array.Fill(colors, SpeakerColor, tokenStart, tokenLength);
                }
                else if (tokenPosition == 2 && primaryCommand is "character" or "live2d" or "stage" or "view" or "camera")
                {
                    Array.Fill(colors, SpeakerColor, tokenStart, tokenLength);
                }
                else
                {
                    HighlightValueSpan(line, colors, tokenStart, tokenLength);
                }
            }
            tokenPosition++;
        }
    }

    private static void HighlightValueSpan(string line, Color[] colors, int start, int length)
    {
        if (length <= 0) return;
        var span = line.AsSpan(start, length);
        if (span[0] == '"')
        {
            Array.Fill(colors, StringColor, start, length);
            return;
        }
        if (span[0] == '$')
        {
            Array.Fill(colors, VariableColor, start, length);
            return;
        }
        if (span.SequenceEqual("true") || span.SequenceEqual("false") || IsNumericList(span))
        {
            for (var i = 0; i < length; i++)
                colors[start + i] = span[i] == ',' ? DelimiterColor : NumberColor;
            return;
        }
        if (span.SequenceEqual("=") || span.SequenceEqual("==") || span.SequenceEqual("!="))
        {
            Array.Fill(colors, DelimiterColor, start, length);
            return;
        }
        for (var i = 0; i < length; i++)
            colors[start + i] = span[i] == ',' ? DelimiterColor : ParamValueColor;
    }

    private static bool IsNumericList(ReadOnlySpan<char> span)
    {
        var hasDigit = false;
        foreach (var ch in span)
        {
            if (char.IsDigit(ch)) hasDigit = true;
            else if (ch is not ('.' or '-' or ',' or '+')) return false;
        }
        return hasDigit;
    }

    private static int FindSpeakerColon(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == ':') return i > start ? i : -1;
            if (char.IsWhiteSpace(ch) || ch is '[' or '#' or '<' or '（' or '(' or '"' or '“') return -1;
        }
        return -1;
    }

    private static void HighlightInlineDialogue(string line, Color[] colors, int start)
    {
        var i = start;
        while (i < line.Length)
        {
            if (line[i] == '[')
            {
                var close = line.IndexOf(']', i + 1);
                if (close > i)
                {
                    Array.Fill(colors, MarkupColor, i, close - i + 1);
                    i = close + 1;
                    continue;
                }
            }
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                var tagEnd = i + 1;
                while (tagEnd < line.Length && !char.IsWhiteSpace(line[tagEnd])) tagEnd++;
                Array.Fill(colors, LineTagColor, i, tagEnd - i);
                i = tagEnd;
                continue;
            }
            if (i + 1 < line.Length && line[i] == '<' && line[i + 1] == '<')
            {
                HighlightCommandSpan(line, colors, i);
                return;
            }
            i++;
        }
    }

    public override void _Draw()
    {
        if (rows.Count == 0) return;

        var start = Math.Max(0, (int)scrollRow);
        var codeHeight = VisibleRows * RowHeight;

        var fileName = Path.GetFileName(file);
        var fileStem = Path.GetFileNameWithoutExtension(file);
        var header = string.IsNullOrEmpty(node) || string.Equals(fileStem, node, StringComparison.Ordinal)
            ? fileName
            : $"{fileName}  ·  {node}";
        DrawShadowedString(new Vector2(24, 36), header, 24, new Color(0.78f, 0.83f, 0.90f, 0.85f));

        // Include an entering row; the display window fades it in while the
        // departing row fades to zero, rather than popping at integer offsets.
        for (var index = start; index < Math.Min(rows.Count, start + VisibleRows + 1); index++)
        {
            var center = (index - scrollRow + 0.5f) * RowHeight;
            var edgeDistance = Math.Min(center, codeHeight - center);
            var edgeFade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(edgeDistance / (2f * RowHeight), 0f, 1f));
            if (edgeFade <= 0) continue;
            var y = HeaderBottom + center + FontSize / 2f;

            var row = rows[index];
            var active = row.Line == activeLine;
            if (active)
            {
                DrawRect(new Rect2(4, HeaderBottom + center - RowHeight / 2f, 6f, RowHeight - 2),
                    new Color(AccentColor, 0.95f * edgeFade));
            }

            var textAlpha = (active ? 1.0f : 0.96f) * edgeFade;
            // Finish every shadow first so a later syntax segment's wider halo
            // cannot darken the neighboring segment's already drawn glyphs.
            foreach (var segment in row.Segments)
                DrawTextEdge(new Vector2(CodeX + segment.X, y), segment.Text, FontSize, textAlpha);
            foreach (var segment in row.Segments)
            {
                DrawString(font, new Vector2(CodeX + segment.X, y), segment.Text, fontSize: FontSize,
                    modulate: new Color(active ? ActiveTextColor : segment.Color, textAlpha));
            }
        }
    }

    private void DrawShadowedString(Vector2 position, string text, int size, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        DrawTextEdge(position, text, size, color.A);
        DrawString(font, position, text, fontSize: size, modulate: color);
    }

    private void DrawTextEdge(Vector2 position, string text, int size, float alpha)
    {
        if (string.IsNullOrEmpty(text)) return;
        // A dense inner edge separates the glyphs from bright artwork; the
        // wider, progressively fainter layers soften it into a black halo.
        var scale = size / (float)FontSize;
        var shadowPosition = position + new Vector2(2f, 3f) * scale;
        DrawStringOutline(font, shadowPosition, text, fontSize: size, size: Math.Max(1, (int)(12 * scale)),
            modulate: new Color(0f, 0f, 0f, alpha * 0.08f));
        DrawStringOutline(font, shadowPosition, text, fontSize: size, size: Math.Max(1, (int)(9 * scale)),
            modulate: new Color(0f, 0f, 0f, alpha * 0.18f));
        DrawStringOutline(font, shadowPosition, text, fontSize: size, size: Math.Max(1, (int)(6 * scale)),
            modulate: new Color(0f, 0f, 0f, alpha * 0.35f));
        DrawStringOutline(font, shadowPosition, text, fontSize: size, size: Math.Max(1, (int)(4 * scale)),
            modulate: new Color(0f, 0f, 0f, alpha * 0.60f));
        DrawString(font, shadowPosition, text, fontSize: size,
            modulate: new Color(0f, 0f, 0f, alpha * 0.85f));
        DrawStringOutline(font, position, text, fontSize: size, size: Math.Max(1, (int)(2 * scale)),
            modulate: new Color(0f, 0f, 0f, alpha * 0.98f));
    }
}

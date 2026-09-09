#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Localization;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed class YarnLineTextProcessor
{
    private readonly MajoLineBreakMarkupReplacer lineBreakMarkupReplacer = new();

    public MajoLineBreakMarkupReplacer LineBreakMarkupReplacer => lineBreakMarkupReplacer;

    public static DialogueLine ConvertPresentedLine(
        LocalizedLine line,
        DialoguePresentationOrigin pendingOrigin,
        out DialoguePresentationOrigin resetOrigin,
        DialogueSpeakerNameResolver? speakerNameResolver,
        ILocaleManager? localeManager,
        string configuredLocale,
        string? runnerLocale,
        string? baseLocale)
    {
        var presentedLocale = ResolvePresentedLocale(line, localeManager, runnerLocale, baseLocale);
        var converted = YarnLineConverter.Convert(
            line,
            presentedLocale,
            speakerNameResolver,
            localeManager?.DialogueLocale ?? configuredLocale);

        if (pendingOrigin == DialoguePresentationOrigin.Normal)
        {
            resetOrigin = DialoguePresentationOrigin.Normal;
            return converted;
        }

        converted = converted with { Origin = pendingOrigin };
        resetOrigin = DialoguePresentationOrigin.Normal;
        return converted;
    }

    public static string ResolvePresentedLocale(
        LocalizedLine line,
        ILocaleManager? localeManager,
        string? runnerLocale,
        string? baseLocale)
    {
        var requested = localeManager?.DialogueLocale ?? runnerLocale ?? "en";
        var chain = localeManager?.GetDialogueFallbackChain(requested) ?? [requested];
        foreach (var candidate in chain)
        {
            var translated = ResolveExactGodotTranslation(line.TextID, candidate);
            if (translated is not null &&
                string.Equals(translated, line.RawText, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return baseLocale ?? requested;
    }

    public static string? ResolveExactGodotTranslation(string lineId, string locale)
    {
        foreach (var translation in TranslationServer.FindTranslations(locale, exact: true))
        {
            var value = translation.GetMessage(new StringName(lineId)).ToString();
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, lineId, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    public LocalizedLine CreateLocalizedLine(
        string lineId,
        string rawText,
        IReadOnlyList<string> substitutions,
        IReadOnlyList<string> metadata,
        string parserLocale)
    {
        var expanded = LineParser.ExpandSubstitutions(rawText, substitutions.ToArray());
        var parser = new LineParser();
        parser.RegisterMarkerProcessor("br", lineBreakMarkupReplacer);
        return new LocalizedLine
        {
            TextID = lineId,
            RawText = rawText,
            Substitutions = substitutions.ToArray(),
            Metadata = metadata.ToArray(),
            Text = parser.ParseString(expanded, parserLocale),
        };
    }
}

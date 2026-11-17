#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Localization;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed class YarnLocalizedDialogue
{
    private readonly ILocaleManager? localeManager;
    private readonly DialogueSpeakerNameResolver? speakerNameResolver;
    private readonly YarnLineTextProcessor textProcessor = new();
    private string configuredLocale = "en";
    private DialogueLine? currentLine;
    private IReadOnlyList<DialogueOption> currentOptions = Array.Empty<DialogueOption>();

    public YarnLocalizedDialogue(ILocaleManager? localeManager, DialogueSpeakerNameResolver? speakerNameResolver)
    {
        this.localeManager = localeManager;
        this.speakerNameResolver = speakerNameResolver;
    }

    public string Locale => configuredLocale;
    public DialogueLine? CurrentLine => currentLine;
    public MajoLineBreakMarkupReplacer LineBreakMarkupReplacer => textProcessor.LineBreakMarkupReplacer;
    public void RecordOptions(IReadOnlyList<DialogueOption> options) => currentOptions = options;
    public void RecordLine(DialogueLine line)
    {
        currentLine = line;
        currentOptions = Array.Empty<DialogueOption>();
    }

    public DialogueLine? ReResolveCurrentLine(YarnProject? project, string currentLineId,
        IReadOnlyList<string> currentSubstitutions, string locale, IReadOnlyList<string> fallbackChain)
    {
        if (string.IsNullOrWhiteSpace(currentLineId) || project is null)
        {
            return null;
        }

        string? rawText = null;
        string? resolvedLocale = null;

        foreach (var candidate in fallbackChain)
        {
            var translated = ResolveExactGodotTranslation(currentLineId, candidate);
            if (translated is not null)
            {
                rawText = translated;
                resolvedLocale = candidate;
                break;
            }
        }

        if (rawText is null && project.baseLocalization is not null)
        {
            rawText = project.baseLocalization.GetLocalizedString(currentLineId);
            resolvedLocale = project.baseLocalization.LocaleCode;
        }

        if (rawText is null)
        {
            return currentLine;
        }

        var localized = CreateLocalizedLine(
            currentLineId,
            rawText,
            currentSubstitutions,
            project.LineMetadata?.GetMetadata(currentLineId) ?? Array.Empty<string>(),
            locale);
        var reResolved = YarnLineConverter.Convert(
            localized,
            resolvedLocale,
            speakerNameResolver,
            locale);

        currentLine = reResolved;
        return reResolved;
    }

    public IReadOnlyList<DialogueOption> ReResolveCurrentOptions(YarnProject? project, string locale, IReadOnlyList<string> fallbackChain)
    {
        if (currentOptions.Count == 0 || project is null)
        {
            return currentOptions;
        }

        var list = new List<DialogueOption>(currentOptions.Count);
        foreach (var option in currentOptions)
        {
            var lineId = option.Line.Id;
            string? rawText = null;
            string? resolvedLocale = null;
            foreach (var candidate in fallbackChain)
            {
                var translated = ResolveExactGodotTranslation(lineId, candidate);
                if (translated is not null)
                {
                    rawText = translated;
                    resolvedLocale = candidate;
                    break;
                }
            }

            if (rawText is null && project.baseLocalization is not null)
            {
                rawText = project.baseLocalization.GetLocalizedString(lineId);
                resolvedLocale = project.baseLocalization.LocaleCode;
            }

            if (rawText is not null)
            {
                var metadata = project.LineMetadata?.GetMetadata(lineId) ?? Array.Empty<string>();
                var substitutions = option.Substitutions ?? Array.Empty<string>();
                var localized = CreateLocalizedLine(lineId, rawText, substitutions, metadata, locale);
                var newLine = YarnLineConverter.Convert(
                    localized,
                    resolvedLocale,
                    speakerNameResolver,
                    locale);
                list.Add(new DialogueOption(option.Id, newLine, option.IsAvailable, substitutions));
            }
            else
            {
                list.Add(option);
            }
        }

        currentOptions = list.AsReadOnly();
        return currentOptions;
    }

    public void SetLocale(string locale, DialogueRunner? runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        configuredLocale = LocaleManager.NormalizeLocale(locale);
        if (runner?.YarnProject is not null)
        {
            if (runner.LineProvider is LineProviderBehaviour provider)
            {
                provider.LocaleCode = configuredLocale;
            }
        }
    }

    public DialogueLine ConvertPresentedLine(LocalizedLine line, DialoguePresentationOrigin origin, DialogueRunner? runner) =>
        YarnLineTextProcessor.ConvertPresentedLine(
            line,
            origin,
            out _,
            speakerNameResolver,
            localeManager,
            configuredLocale,
            runner?.LineProvider.LocaleCode,
            runner?.YarnProject?.baseLocalization?.LocaleCode);

    private LocalizedLine CreateLocalizedLine(
        string lineId,
        string rawText,
        IReadOnlyList<string> substitutions,
        IReadOnlyList<string> metadata,
        string parserLocale) =>
        textProcessor.CreateLocalizedLine(lineId, rawText, substitutions, metadata, parserLocale);

    private static string? ResolveExactGodotTranslation(string lineId, string locale) =>
        YarnLineTextProcessor.ResolveExactGodotTranslation(lineId, locale);

}

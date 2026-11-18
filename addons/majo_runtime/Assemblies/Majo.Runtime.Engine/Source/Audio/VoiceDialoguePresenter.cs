#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio;

public sealed class VoiceDialoguePresenter : IDialoguePresenter
{
    public const string MetadataPrefix = "voice:";
    private readonly IAudioManager audio;
    private readonly ILocaleManager? locales;
    private readonly ILocalizedResourceResolver? resources;

    public VoiceDialoguePresenter(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public VoiceDialoguePresenter(
        IAudioManager audio,
        ILocaleManager locales,
        ILocalizedResourceResolver resources)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.locales = locales ?? throw new ArgumentNullException(nameof(locales));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public string? LastVoiceId { get; private set; }

    public async Task PresentLineAsync(
        DialogueLine line,
        DialoguePresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(context);
        await audio.StopVoiceAsync(cancellationToken: context.NextLineToken);
        if (line.Origin == DialoguePresentationOrigin.HotReloadReplay)
        {
            LastVoiceId = null;
            return;
        }
        var resolution = locales is not null && resources is not null
            ? DialogueVoiceResolver.ResolveExistingVoice(line, audio, locales, resources)
            : null;
        var voiceId = resolution?.VoiceId
            ?? (locales is null || resources is null
                ? DialogueVoiceResolver.ResolveExistingVoiceId(line, audio)
                : null);
        if (voiceId is null)
        {
            LastVoiceId = null;
            return;
        }

        var result = await audio.PlayVoiceAsync(
            resolution?.ResolvedPath ?? voiceId,
            cancellationToken: context.NextLineToken);
        LastVoiceId = result.IsSkipped ? null : voiceId;
    }

    public async Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        await audio.StopVoiceAsync(cancellationToken: cancellationToken);
        LastVoiceId = null;
    }
}

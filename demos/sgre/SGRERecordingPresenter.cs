#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.SGRE;

/// <summary>Advance the demo at reading pace using the movie's simulation clock.</summary>
public partial class SGRERecordingPresenter : Node, IDialoguePresenter
{
    public int LineCount { get; private set; }

    public async Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
    {
        LineCount++;
        var voiceId = line.Metadata.FirstOrDefault(tag => tag.StartsWith("voice:", StringComparison.OrdinalIgnoreCase));
        var voiceSeconds = 0.0;
        if (voiceId is not null && !voiceId.Equals("voice:none", StringComparison.OrdinalIgnoreCase))
        {
            var path = $"res://demos/sgre/assets/audio/voice/{voiceId[6..]}.wav";
            var voice = ResourceLoader.Load<AudioStream>(path);
            if (voice is not null) voiceSeconds = voice.GetLength();
        }
        var readSeconds = line.Text.Text.Length / 9.0 + 0.7;
        var duration = Math.Max(Math.Max(voiceSeconds + 0.5, readSeconds), 1.8);
        GD.Print($"SGRE_RECORD_LINE {LineCount:D2} voice={voiceSeconds:F2} duration={duration:F2}");
        await ToSignal(GetTree().CreateTimer(duration), SceneTreeTimer.SignalName.Timeout);
        if (!context.NextLineToken.IsCancellationRequested) context.RequestNextLine();
    }

    public Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken) =>
        Task.FromResult(options.FirstOrDefault(option => option.IsAvailable));
}

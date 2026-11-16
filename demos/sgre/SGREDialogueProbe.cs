#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.SGRE;

public partial class SGREDialogueProbe : Node, IDialoguePresenter
{
    public int LineCount { get; private set; }
    public bool Capture { get; init; }

    public async Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
    {
        LineCount++;
        if (Capture && (LineCount == 3 || LineCount == 6))
        {
            _ = CaptureTransitionAsync(LineCount);
            context.RequestNextLine();
            return;
        }
        if (Capture && (LineCount == 2 || LineCount == 4 || LineCount == 7 || LineCount == 14 || LineCount == 16 || LineCount == 17 || LineCount == 18))
        {
            var directory = ProjectSettings.GlobalizePath("res://artifacts/sgre");
            Directory.CreateDirectory(directory);
            if (LineCount == 4)
            {
                await Task.Delay(400, context.NextLineToken);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                using var first = GetViewport().GetTexture().GetImage();
                first.SavePng(Path.Combine(directory, "line-04-motion-a.png"));
            }
            await Task.Delay(LineCount == 16 ? 2200 : LineCount == 4 ? 400 : 800, context.NextLineToken);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            using var image = GetViewport().GetTexture().GetImage();
            image.SavePng(Path.Combine(directory, $"line-{LineCount:D2}.png"));
        }
        else
        {
            await Task.Delay(120, context.NextLineToken);
        }
        context.RequestNextLine();
    }

    private async Task CaptureTransitionAsync(int line)
    {
        var directory = ProjectSettings.GlobalizePath("res://artifacts/sgre");
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 6 && IsInsideTree(); i++)
        {
            await Task.Delay(150);
            if (!IsInsideTree()) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            using var image = GetViewport().GetTexture().GetImage();
            image.SavePng(Path.Combine(directory, $"transition-{line:D2}-{i:D2}.png"));
        }
    }

    public Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken) =>
        Task.FromResult(options.FirstOrDefault(option => option.IsAvailable));
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Demos.AceAttorney.Actors;

public sealed class SpriteSheetCharacterActor : GodotCharacterActorBase, IDisposable
{
    private readonly SpriteSheetManifest manifest;
    private readonly string directory;
    private readonly SpriteSheetPlaybackNode playback;
    private readonly SpriteSheetActorRegistry registry;
    private bool initialized;

    public SpriteSheetCharacterActor(
        ActorDefinition definition, Node2D view, CharacterPositionMap positions,
        SpriteSheetManifest manifest, string directory, CharacterPositionPreset initialPosition,
        SpriteSheetActorRegistry registry)
        : base(definition, view, positions, initialPosition, manifest.DefaultPose)
    {
        this.manifest = manifest;
        this.directory = directory;
        this.registry = registry;
        playback = new SpriteSheetPlaybackNode { Name = "SpritePlayback", Centered = false };
        view.AddChild(playback);
        SetAppearance(manifest.DefaultPose);
        registry.Register(this);
    }

    public override void SetAppearance(string appearance) => PersistentWrite(() =>
    {
        EnsureAlive();
        var pose = manifest.GetPose(appearance);
        playback.SetPose(pose, directory, manifest.Canvas, playIntro: initialized && Appearance != appearance);
        initialized = true;
        InvalidateAppearance();
        Appearance = appearance;
    });

    public void SetSpeaking(bool speaking)
    {
        if (IsAlive) playback.SetSpeaking(speaking);
    }

    public override Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = manifest.GetPose(appearance);
        return Task.CompletedTask;
    }

    public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance, string appearance, AnimationSpec animation,
        ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = manifest.GetPose(appearance);
        return Task.FromResult<IPreparedActorAppearance>(new PreparedPose(this, appearance));
    }

    protected override void OnDestroy()
    {
        registry.Unregister(this);
        playback.SetProcess(false);
        playback.ReleaseTextures();
    }

    public void Dispose() => Destroy();

    private sealed class PreparedPose(SpriteSheetCharacterActor actor, string pose) : IPreparedActorAppearance
    {
        public string ResolvedAppearance => pose;
        public void Commit() => actor.SetAppearance(pose);
        public void ApplyProgress(float progress) => _ = progress;
        public void Finish() { }
        public void Dispose() { }
    }
}

public partial class SpriteSheetPlaybackNode : Sprite2D
{
    private SpriteSheetPose? pose;
    private SpriteSheetClip? clip;
    private Texture2D[] pages = [];
    private readonly Dictionary<SpriteSheetFrame, AtlasTexture> frameTextures = new();
    private int[] canvas = [];
    private int introIndex = -1;
    private int frameIndex;
    private double frameMs;
    private double holdMs;
    private bool introHoldApplied;
    private bool speaking;

    public void SetPose(SpriteSheetPose value, string directory, int[] canvasSize, bool playIntro)
    {
        pose = value;
        canvas = canvasSize;
        ReleaseTextures();
        pages = value.Pages.Select(path => ResourceLoader.Load<Texture2D>($"{directory}/{path}") ??
            throw new ActorException($"Missing sprite atlas: {directory}/{path}")).ToArray();
        introIndex = playIntro && value.Intro.Count > 0 ? 0 : -1;
        StartClip(introIndex >= 0 ? value.Intro[0] : speaking ? value.Speak : value.Idle);
        SetProcess(true);
    }

    public void SetSpeaking(bool value)
    {
        if (speaking == value) return;
        speaking = value;
        if (pose is not null && introIndex < 0)
            StartClip(value ? pose.Speak : pose.Idle);
    }

    public override void _Process(double delta)
    {
        if (pose is null || clip is null) return;
        var elapsed = delta * 1000;
        // Bound catch-up after a paused or stalled frame.
        for (var step = 0; step < 128 && elapsed > 0; step++)
        {
            var remaining = frameMs + holdMs;
            if (elapsed < remaining)
            {
                if (frameMs > 0)
                {
                    var consumed = Math.Min(frameMs, elapsed);
                    frameMs -= consumed;
                    elapsed -= consumed;
                }
                holdMs -= elapsed;
                break;
            }
            elapsed -= remaining;
            frameMs = holdMs = 0;
            Advance();
        }
    }

    private void StartClip(SpriteSheetClip value)
    {
        clip = value;
        frameIndex = 0;
        holdMs = 0;
        introHoldApplied = false;
        ShowFrame(value.Frames[0]);
    }

    private void Advance()
    {
        if (clip is null || pose is null) return;
        frameIndex++;
        if (frameIndex < clip.Frames.Count)
        {
            ShowFrame(clip.Frames[frameIndex]);
            return;
        }
        if (introIndex >= 0)
        {
            var duration = clip.Frames.Sum(frame => frame.DurationMs);
            var extra = Math.Max(0, clip.NextPoseDelayMs - duration);
            if (extra > 0 && !introHoldApplied)
            {
                introHoldApplied = true;
                holdMs = extra;
                return;
            }
            introIndex++;
            if (introIndex < pose.Intro.Count)
            {
                StartClip(pose.Intro[introIndex]);
                return;
            }
            introIndex = -1;
        }
        StartClip(speaking ? pose.Speak : pose.Idle);
    }

    private void ShowFrame(SpriteSheetFrame frame)
    {
        if (!frameTextures.TryGetValue(frame, out var texture))
        {
            var rect = frame.Rect;
            texture = new AtlasTexture
            {
                Atlas = pages[frame.Page],
                Region = new Rect2(rect[0], rect[1], rect[2], rect[3]),
                FilterClip = true,
            };
            frameTextures.Add(frame, texture);
        }
        Texture = texture;
        Offset = new Vector2(frame.Offset[0] - canvas[0] / 2f,
            frame.Offset[1] - canvas[1]);
        frameMs = frame.DurationMs;
    }

    public void ReleaseTextures()
    {
        Texture = null;
        foreach (var texture in frameTextures.Values) texture.Dispose();
        frameTextures.Clear();
        pages = [];
    }
}

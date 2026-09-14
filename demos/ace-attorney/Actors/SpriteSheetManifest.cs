#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Majo.Demos.AceAttorney.Actors;

public sealed class SpriteSheetManifest
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("canvas")] public int[] Canvas { get; init; } = [];
    [JsonPropertyName("default_pose")] public string DefaultPose { get; init; } = string.Empty;
    [JsonPropertyName("poses")] public List<SpriteSheetPose> Poses { get; init; } = [];

    public static SpriteSheetManifest Parse(string json)
    {
        var result = JsonSerializer.Deserialize<SpriteSheetManifest>(json) ??
            throw new FormatException("Sprite sheet manifest is empty.");
        if (result.SchemaVersion != 1 || result.Canvas.Length != 2 ||
            result.Canvas[0] <= 0 || result.Canvas[1] <= 0 ||
            result.Poses.Count == 0 ||
            !result.Poses.Exists(p => p.Id == result.DefaultPose))
            throw new FormatException($"Invalid sprite sheet manifest for '{result.Id}'.");
        foreach (var pose in result.Poses)
        {
            if (string.IsNullOrWhiteSpace(pose.Id) || pose.Pages.Count == 0 ||
                pose.Idle.Frames.Count == 0 || pose.Speak.Frames.Count == 0)
                throw new FormatException($"Invalid pose '{pose.Id}' in '{result.Id}'.");
            foreach (var clip in new[] { pose.Idle, pose.Speak }) ValidateClip(clip, pose);
            foreach (var clip in pose.Intro) ValidateClip(clip, pose);
        }
        return result;
    }

    private static void ValidateClip(SpriteSheetClip clip, SpriteSheetPose pose)
    {
        if (clip.Frames.Count == 0)
            throw new FormatException($"Empty clip in pose '{pose.Id}'.");
        foreach (var frame in clip.Frames)
        {
            if (frame.Page < 0 || frame.Page >= pose.Pages.Count ||
                frame.Rect.Length != 4 || frame.Offset.Length != 2 ||
                frame.Rect[2] <= 0 || frame.Rect[3] <= 0 || frame.DurationMs <= 0)
                throw new FormatException($"Invalid frame in pose '{pose.Id}'.");
        }
    }

    public SpriteSheetPose GetPose(string id) =>
        Poses.Find(pose => pose.Id == id) ??
        throw new ArgumentException($"Unknown sprite sheet pose '{id}' in '{Id}'.", nameof(id));
}

public sealed class SpriteSheetPose
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("pages")] public List<string> Pages { get; init; } = [];
    [JsonPropertyName("idle")] public SpriteSheetClip Idle { get; init; } = new();
    [JsonPropertyName("speak")] public SpriteSheetClip Speak { get; init; } = new();
    [JsonPropertyName("intro")] public List<SpriteSheetClip> Intro { get; init; } = [];
}

public sealed class SpriteSheetClip
{
    [JsonPropertyName("frames")] public List<SpriteSheetFrame> Frames { get; init; } = [];
    [JsonPropertyName("next_pose_delay_ms")] public int NextPoseDelayMs { get; init; }
    [JsonPropertyName("no_speak_delay")] public bool NoSpeakDelay { get; init; }
}

public sealed class SpriteSheetFrame
{
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("rect")] public int[] Rect { get; init; } = [];
    [JsonPropertyName("offset")] public int[] Offset { get; init; } = [];
    [JsonPropertyName("duration_ms")] public int DurationMs { get; init; }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Godot;

namespace Majo.Demos.SGRE.Emote;

/// <summary>Evaluates E-mote layers and draws their deformed meshes each frame.</summary>
public partial class EmoteRig : Node2D
{
    private const string Root = "res://demos/sgre/assets/characters/红莉栖/emote";
    private static readonly int[] Indices = BuildIndices();
    private readonly List<EmoteClip> parts = [];
    private readonly Texture2D[] textures = new Texture2D[4];
    private readonly CanvasItemMaterial additive = new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    private readonly ShaderMaterial multiply = new()
    {
        Shader = new Shader { Code = "shader_type canvas_item; render_mode blend_mul, unshaded; void fragment() { vec4 source = COLOR; COLOR = vec4(mix(vec3(1.0), source.rgb, source.a), 1.0); }" },
    };
    private JavaScriptCore? engine;
    private Vector3 layout;
    private ulong startedAt;
    private double totalEvaluationMilliseconds;
    private float lastGeometryChecksum;
    private bool hasChecksum;
    public long EvaluatedFrames { get; private set; }
    public int LastPartCount { get; private set; }
    public int LastMaskCount { get; private set; }
    public double EvaluationMilliseconds { get; private set; }
    public double AverageEvaluationMilliseconds => EvaluatedFrames == 0 ? 0 : totalEvaluationMilliseconds / EvaluatedFrames;
    public double EvaluatedFps => startedAt == 0 ? 0 : EvaluatedFrames * 1_000_000.0 / Math.Max(1, Time.GetTicksUsec() - startedAt);
    public int ChangedFrames { get; private set; }
    public int MotionCount { get; private set; }
    public int TalkingStarts { get; private set; }

    public override void _Ready()
    {
        var dimensions = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (var i = 0; i < textures.Length; i++)
        {
            textures[i] = ResourceLoader.Load<Texture2D>($"{Root}/tex_{i:D3}.png") ??
                throw new InvalidOperationException($"Missing E-mote texture {i}.");
            dimensions[$"tex#{i:D3}"] = [textures[i].GetWidth(), textures[i].GetHeight()];
        }
        engine = new JavaScriptCore();
        engine.Evaluate(FileAccess.GetFileAsString("res://demos/sgre/emote/emotePlayer.js"));
        engine.Evaluate(FileAccess.GetFileAsString("res://demos/sgre/emote/bridge.js"));
        var model = FileAccess.GetFileAsString($"{Root}/model.json");
        var output = engine.EvaluateString($"emoteInit(JSON.parse({JsonSerializer.Serialize(model)}), {JsonSerializer.Serialize(dimensions)})");
        var values = JsonSerializer.Deserialize<float[]>(output) ?? throw new InvalidOperationException("E-mote layout is missing.");
        layout = new Vector3(values[0], values[1], values[2]);
        SetProcess(true);
    }

    public void SetExpression(string expression) => Execute("emoteExpression", expression);
    public void PlayMotion(string motion)
    {
        if (motion == "stop") engine?.Evaluate("emoteStopMotion()");
        else { Execute("emoteMotion", motion); MotionCount++; }
    }
    public void SetTalking(bool value)
    {
        if (value) TalkingStarts++;
        engine?.Evaluate($"emoteTalking({(value ? "true" : "false")})");
    }
    private void Execute(string method, string argument)
    {
        if (engine is null) throw new InvalidOperationException("E-mote model is not ready.");
        engine.Evaluate($"{method}({JsonSerializer.Serialize(argument)})");
    }

    public override void _Process(double delta)
    {
        if (engine is null) return;
        var start = Time.GetTicksUsec();
        if (startedAt == 0) startedAt = start;
        var frame = engine.EvaluateFloatArray($"emoteFrame({Math.Min(delta, 0.1).ToString("R", CultureInfo.InvariantCulture)})");
        var count = (int)frame[0];
        var maskCount = (int)frame[1];
        while (parts.Count < count)
        {
            // Direction's subject viewport draws layer 2. These nodes are added
            // after the host has assigned layers to the original actor tree.
            var clip = new EmoteClip { Name = $"Part{parts.Count:D3}", VisibilityLayer = 2 };
            parts.Add(clip);
            AddChild(clip);
            clip.Initialize();
        }
        var maskParts = new List<EmotePart>[maskCount];
        for (var i = 0; i < maskCount; i++) maskParts[i] = [];
        var cursor = 2;
        for (var i = 0; i < count; i++)
        {
            var texture = (int)frame[cursor++];
            var left = frame[cursor++];
            var top = frame[cursor++];
            var width = frame[cursor++];
            var height = frame[cursor++];
            var color = new Color(frame[cursor++], frame[cursor++], frame[cursor++], frame[cursor++]);
            var blend = (int)frame[cursor++];
            var mask = (int)frame[cursor++];
            var maskBits = (int)frame[cursor++];
            if (texture < 0 || texture >= textures.Length) throw new InvalidOperationException($"Invalid E-mote texture {texture}.");
            var clip = parts[i];
            clip.Visible = true;
            clip.MaskIndex = mask;
            var part = clip.Part;
            part.Texture = textures[texture];
            part.Material = blend == 272 ? additive : blend is 19 or 274 ? multiply : null;
            part.SetGeometry(frame, cursor, layout, left, top, width, height, color, Indices);
            cursor += 162;
            for (var m = 0; m < maskCount; m++)
                if ((maskBits & (1 << m)) != 0) maskParts[m].Add(part);
        }
        for (var i = 0; i < count; i++)
            parts[i].SetMask(parts[i].MaskIndex < 0 ? null : maskParts[parts[i].MaskIndex]);
        for (var i = count; i < parts.Count; i++) parts[i].Visible = false;
        var checksum = 0f;
        for (var i = 14; i < frame.Length; i += 47) checksum += frame[i];
        if (hasChecksum && Math.Abs(checksum - lastGeometryChecksum) > 0.01f) ChangedFrames++;
        lastGeometryChecksum = checksum;
        hasChecksum = true;
        LastPartCount = count;
        LastMaskCount = maskCount;
        EvaluatedFrames++;
        EvaluationMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
        totalEvaluationMilliseconds += EvaluationMilliseconds;
    }

    private static int[] BuildIndices()
    {
        var indices = new int[8 * 8 * 6];
        var i = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var a = y * 9 + x;
                indices[i++] = a; indices[i++] = a + 1; indices[i++] = a + 9;
                indices[i++] = a + 1; indices[i++] = a + 10; indices[i++] = a + 9;
            }
        return indices;
    }

    public override void _ExitTree()
    {
        SetProcess(false);
        engine?.Dispose(); engine = null;
        additive.Dispose(); multiply.Shader?.Dispose(); multiply.Dispose();
    }
}

public partial class EmoteClip : Node2D
{
    private IReadOnlyList<EmotePart>? masks;
    public int MaskIndex { get; set; }
    public EmotePart Part { get; private set; } = null!;
    public void Initialize()
    {
        Part = new EmotePart { VisibilityLayer = 2 };
        AddChild(Part);
    }
    public void SetMask(IReadOnlyList<EmotePart>? value)
    {
        masks = value;
        ClipChildren = value is null ? ClipChildrenMode.Disabled : ClipChildrenMode.Only;
        QueueRedraw();
    }
    public override void _Draw()
    {
        if (masks is null) return;
        foreach (var mask in masks) mask.DrawOn(GetCanvasItem());
    }
}

public partial class EmotePart : Node2D
{
    private readonly Vector2[] points = new Vector2[81];
    private readonly Vector2[] uvs = new Vector2[81];
    private readonly Color[] colors = [Colors.White];
    private int[] indices = [];
    public Texture2D? Texture { get; set; }
    public void SetGeometry(float[] data, int start, Vector3 layout, float left, float top,
        float width, float height, Color color, int[] triangleIndices)
    {
        var texture = Texture ?? throw new InvalidOperationException("E-mote part has no texture.");
        for (var i = 0; i < 81; i++)
        {
            points[i] = new Vector2(data[start + i * 2] * layout.X + layout.Y - 300,
                data[start + i * 2 + 1] * layout.X + layout.Z - 900);
            var u = (left + 0.25f + i % 9 / 8f * (width - 0.5f)) / texture.GetWidth();
            var v = (top + 0.25f + i / 9 / 8f * (height - 0.5f)) / texture.GetHeight();
            uvs[i] = new Vector2(u, v);
        }
        colors[0] = color;
        indices = triangleIndices;
        QueueRedraw();
    }
    public void DrawOn(Rid canvas)
    {
        if (Texture is not null && indices.Length != 0)
            RenderingServer.CanvasItemAddTriangleArray(canvas, indices, points, colors, uvs, [], [], Texture.GetRid());
    }
    public override void _Draw() => DrawOn(GetCanvasItem());
}

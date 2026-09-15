#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Script;

namespace Majo.Demos.Manosaba;

/// <summary>Opt-in full Trial00 progression trace. Never skips transition settlement.</summary>
public partial class Trial00ProgressProbe : Node
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DateTimeOffset epoch = DateTimeOffset.UtcNow;
    private readonly List<Frame> frames = new(24000);
    private readonly List<object> lines = new();
    private readonly List<Viewport> measuredViewports = new();
    private readonly ConcurrentQueue<Span> spans = new();
    private ActivityListener? listener;
    private IDialoguePlayer? dialogue;
    private IScriptPlayback? playback;
    private double previous;
    private double lineStarted;
    private double lastAction;
    private int lineNumber;
    private bool finished;
    private bool recording = true;
    private bool rapid;

    public override void _Ready()
    {
        rapid = Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-progress-rapid") >= 0;
        GetWindow().Title = "Trial00 Progress Trace — automatic dialogue, keep focused";
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimePerformanceTrace.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Enqueue(new Span(
                (activity.StartTimeUtc - epoch.UtcDateTime).TotalMilliseconds,
                activity.Duration.TotalMilliseconds, activity.OperationName,
                activity.Id, activity.ParentId, activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value))),
        };
        ActivitySource.AddActivityListener(listener);
        TrackViewport(GetViewport());
        GetTree().NodeAdded += TrackViewport;
        GetTree().NodeRemoved += UntrackViewport;
    }

    private void TrackViewport(Node node)
    {
        if (node is not Viewport viewport) return;
        measuredViewports.Add(viewport);
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
    }

    private void UntrackViewport(Node node)
    {
        if (node is Viewport viewport) measuredViewports.Remove(viewport);
    }

    public void Attach(MajoRuntimeBootstrap runtime)
    {
        dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
        playback = runtime.Host.Services.Get<IScriptPlayback>();
        dialogue.LinePresented += OnLine;
    }

    private void OnLine(DialogueLine line)
    {
        lineNumber++;
        lineStarted = clock.Elapsed.TotalMilliseconds;
        lines.Add(new { timeMs = lineStarted, number = lineNumber, line.Id, line.Speaker });
    }

    public override void _Process(double delta)
    {
        if (!recording) return;
        var now = clock.Elapsed.TotalMilliseconds;
        var gpu = 0.0;
        var renderCpu = RenderingServer.GetFrameSetupTimeCpu();
        foreach (var viewport in measuredViewports)
        {
            if (viewport is SubViewport sub && sub.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled) continue;
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport.GetViewportRid());
            renderCpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport.GetViewportRid());
        }
        var textureMiB = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576;
        // Some Metal builds expose a wrapped unsigned memory counter. Preserve it
        // as unavailable rather than reporting petabytes or guessing a correction.
        if (textureMiB < 0 || textureMiB > 1048576) textureMiB = double.NaN;
        if (previous > 0)
            frames.Add(new Frame(now, now - previous, DisplayServer.WindowIsFocused(), lineNumber,
                playback?.Phase ?? ScriptPlaybackPhase.Idle, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalAllocatedBytes(false), Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000,
                Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
                textureMiB,
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw), gpu, renderCpu));
        previous = now;
        if (playback is null || dialogue is null) return;
        if (playback.Phase == ScriptPlaybackPhase.PresentingOptions) { finished = true; return; }
        if (now - lastAction < (rapid ? 100 : 500)) return;
        var age = now - lineStarted;
        // Let the initial 25-second drift finish in both modes. Rapid progression
        // only reduces dialogue dwell; script waits and transition rules remain intact.
        var dwell = lineNumber == 1 ? 28000 : rapid ? 100 : 2000;
        if (playback.Phase == ScriptPlaybackPhase.PresentingLine && age > (rapid ? 100 : 750))
        {
            lastAction = now;
            dialogue.RequestHurryUp();
        }
        else if (playback.Phase == ScriptPlaybackPhase.AwaitingAdvance && age >= dwell)
        {
            lastAction = now;
            dialogue.RequestNextLine();
        }
    }

    public async Task RunAsync(MajoRuntimeBootstrap runtime)
    {
        while (!finished && clock.Elapsed.TotalSeconds < 150)
            await ToSignal(GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
        recording = false;
        listener?.Dispose();
        if (dialogue is not null) dialogue.LinePresented -= OnLine;
        var directory = ProjectSettings.GlobalizePath("res://artifacts/trial00-progress/" +
            epoch.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        using (var writer = new StreamWriter(Path.Combine(directory, "frames.csv")))
        {
            writer.WriteLine("time_ms,gap_ms,focused,line,phase,gc0,gc1,gc2,allocated_bytes,process_ms,draw_calls,texture_mib,pipeline_canvas,pipeline_draw,gpu_ms,render_cpu_ms");
            foreach (var f in frames)
                writer.WriteLine(FormattableString.Invariant($"{f.Time:F3},{f.Gap:F3},{f.Focused},{f.Line},{f.Phase},{f.Gc0},{f.Gc1},{f.Gc2},{f.Allocated},{f.ProcessMs:F3},{f.DrawCalls},{f.TextureMiB:F3},{f.CanvasPipelines},{f.DrawPipelines},{f.GpuMs:F3},{f.RenderCpuMs:F3}"));
        }
        Write("spans.json", spans.ToArray());
        Write("lines.json", lines);
        Write("events.json", runtime.RingBuffer!.RecentEvents.Select(e => new
        {
            timeMs = (e.Timestamp - epoch).TotalMilliseconds, e.Category, e.EventId, e.Message, e.Correlation, e.Properties,
        }).ToArray());
        var configuration = new
        {
            completed = finished, lineCount = lineNumber, seconds = clock.Elapsed.TotalSeconds,
            progressionMode = rapid ? "rapid" : "normal", actionIntervalMs = rapid ? 100 : 500,
            lineDwellMs = rapid ? 100 : 2000, openingDwellMs = 28000,
            maxFps = Engine.MaxFps, vsync = DisplayServer.WindowGetVsyncMode().ToString(),
            refreshHz = DisplayServer.ScreenGetRefreshRate(), windowSize = GetWindow().Size.ToString(),
            renderer = RenderingServer.GetCurrentRenderingMethod().ToString(), epoch,
            focusPercent = 100.0 * frames.Count(f => f.Focused) / frames.Count,
            spans = spans.Count, logs = runtime.RingBuffer.Count, logCapacity = runtime.RingBuffer.Capacity,
        };
        Write("configuration.json", configuration);
        using (var image = GetViewport().GetTexture().GetImage()) image.SavePng(Path.Combine(directory, "final.png"));
        GD.Print("TRIAL00_PROGRESS_COMPLETE " + directory + " " + JsonSerializer.Serialize(configuration));
        await runtime.RequestShutdownAsync();
        GetTree().Quit(finished ? 0 : 1);
        void Write(string filename, object value) => File.WriteAllText(Path.Combine(directory, filename), JsonSerializer.Serialize(value, JsonOptions));
    }

    public override void _ExitTree()
    {
        listener?.Dispose();
        GetTree().NodeAdded -= TrackViewport;
        GetTree().NodeRemoved -= UntrackViewport;
        if (dialogue is not null) dialogue.LinePresented -= OnLine;
    }

    private readonly record struct Frame(double Time, double Gap, bool Focused, int Line, ScriptPlaybackPhase Phase,
        int Gc0, int Gc1, int Gc2, long Allocated, double ProcessMs, double DrawCalls, double TextureMiB,
        double CanvasPipelines, double DrawPipelines, double GpuMs, double RenderCpuMs);
    private sealed record Span(double StartMs, double DurationMs, string Name, string? Id, string? Parent,
        Dictionary<string, object?> Tags);
}

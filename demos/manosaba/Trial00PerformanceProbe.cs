#nullable enable

using System.Diagnostics;
using Godot;

namespace Majo.Demos.Manosaba;

/// <summary>Opt-in real-renderer startup calibration; enabled by --trial00-profile.</summary>
public partial class Trial00PerformanceProbe : Node
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double previous;
    private double nextSample;
    private int frame;
    private readonly System.Collections.Generic.List<(double Time, double Gap, bool Focused)> frames = new();

    public override void _Ready() => GetTree().NodeAdded += OnNodeAdded;

    public override void _ExitTree() => GetTree().NodeAdded -= OnNodeAdded;

    private void OnNodeAdded(Node node)
    {
        if (node is SubViewport viewport)
            GD.Print($"TRIAL00_VIEWPORT t_ms={clock.Elapsed.TotalMilliseconds:F1} name={viewport.Name} size={viewport.Size}");
    }

    public override void _Process(double delta)
    {
        var now = clock.Elapsed.TotalMilliseconds;
        var interval = now - previous;
        previous = now;
        frame++;
        frames.Add((now, interval, DisplayServer.WindowIsFocused()));
        if (interval < 50 && now < nextSample) return;
        nextSample = now + 500;
        using var process = Process.GetCurrentProcess();
        GD.Print($"TRIAL00_PERF t_ms={now:F1} frame={frame} gap_ms={interval:F1} " +
            $"rss_mib={process.WorkingSet64 / 1048576.0:F1} " +
            $"managed_mib={System.GC.GetTotalMemory(false) / 1048576.0:F1} " +
            $"static_mib={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F1} " +
            $"video_mib={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F1} " +
            $"gc0={System.GC.CollectionCount(0)} gc1={System.GC.CollectionCount(1)} gc2={System.GC.CollectionCount(2)} " +
            $"pipeline_draw={Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)} " +
            $"pipeline_canvas={Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas)} " +
            $"texture_mib={Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0:F1}");
        if (now >= 20000)
        {
            SetProcess(false);
            var directory = ProjectSettings.GlobalizePath("res://build/trial00-performance");
            System.IO.Directory.CreateDirectory(directory);
            var filename = System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-profile-screenshot") >= 0)
            {
                using var screenshot = GetViewport().GetTexture().GetImage();
                screenshot.SavePng(System.IO.Path.Combine(directory, $"screen-{filename}.png"));
            }
            using var writer = new System.IO.StreamWriter(System.IO.Path.Combine(directory, $"frames-{filename}.csv"));
            writer.WriteLine("time_ms,gap_ms,focused");
            foreach (var sample in frames)
                writer.WriteLine(System.FormattableString.Invariant($"{sample.Time:F3},{sample.Gap:F3},{sample.Focused}"));
        }
    }
}

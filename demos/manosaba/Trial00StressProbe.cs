#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Direction.Godot;

namespace Majo.Demos.Manosaba;

/// <summary>Opt-in fixed-camera rendered stress audit; never changes normal game settings.</summary>
public partial class Trial00StressProbe : Node
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<Sample> samples = new();
    private readonly List<object> results = new();
    private double previous;
    private bool sampling = true;
    private SubViewport[] viewports = [];
    private string directory = "";
    private GodotDirectionRenderHost renderHost = null!;

    public override void _Process(double delta)
    {
        var now = clock.Elapsed.TotalMilliseconds;
        if (sampling && previous > 0)
            samples.Add(new Sample(now, now - previous, DisplayServer.WindowIsFocused()));
        previous = now;
    }

    public async Task RunAsync(MajoRuntimeBootstrap runtime)
    {
        GetWindow().Title = "Trial00 Stress Audit — keep this window focused";
        var originalVsync = DisplayServer.WindowGetVsyncMode();
        var originalCap = Engine.MaxFps;
        var baselineRequests = new List<IDisposable>();
        renderHost = runtime.Host!.Services.Get<GodotDirectionRenderHost>();
        directory = ProjectSettings.GlobalizePath("res://artifacts/trial00-stress/" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            // Opening drift lasts 25 seconds; leave its entire motion in startup data.
            await ToSignal(GetTree().CreateTimer(28), SceneTreeTimer.SignalName.Timeout);
            SaveFrames("startup");
            samples.Clear();
            sampling = false;
            viewports = runtime.FindChildren("*", "SubViewport", true, false).Cast<SubViewport>().ToArray();
            var config = new
            {
                maxFps = Engine.MaxFps, vsync = originalVsync.ToString(),
                refreshHz = DisplayServer.ScreenGetRefreshRate(), lowProcessor = OS.LowProcessorUsageMode,
                frameDelay = ProjectSettings.GetSetting("application/run/frame_delay", 0).AsInt32(),
                windowSize = GetWindow().Size.ToString(), renderer = RenderingServer.GetCurrentRenderingMethod().ToString(),
                viewDemand = renderHost.InspectViewDemand(),
                viewports = viewports.Select(v => new { path = v.GetPath().ToString(), size = v.Size.ToString(), mode = v.RenderTargetUpdateMode.ToString() }).ToArray(),
            };
            File.WriteAllText(Path.Combine(directory, "configuration.json"), JsonSerializer.Serialize(config, JsonOptions));
            GD.Print("TRIAL00_STRESS_CONFIG " + JsonSerializer.Serialize(config));
            foreach (var viewport in viewports)
                RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
            await MeasureAsync("default");
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            await MeasureAsync("uncapped");

            // Exercise the normal demand contract for the all-on baseline, without
            // introducing a competing mode writer into the diagnostic.
            foreach (var id in renderHost.ViewIds)
                baselineRequests.Add(renderHost.AcquireViewDemand(renderHost.RequireView(id), "stress baseline"));
            await MeasureAsync("all-views-requested");
            foreach (var request in baselineRequests) request.Dispose();
            baselineRequests.Clear();
            await MeasureAsync("scheduled-repeat");
            foreach (var id in renderHost.ViewIds)
                baselineRequests.Add(renderHost.AcquireViewDemand(renderHost.RequireView(id), "stress baseline"));
            await MeasureAsync("all-views-requested-repeat");
            foreach (var request in baselineRequests) request.Dispose();
            baselineRequests.Clear();
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, JsonOptions));
            GD.Print($"TRIAL00_STRESS_COMPLETE {directory}");
        }
        finally
        {
            sampling = false;
            foreach (var request in baselineRequests) request.Dispose();
            DisplayServer.WindowSetVsyncMode(originalVsync);
            Engine.MaxFps = originalCap;
            await runtime.RequestShutdownAsync();
        }
        GetTree().Quit();
    }

    private async Task MeasureAsync(string name)
    {
        sampling = false;
        await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
        samples.Clear();
        sampling = true;
        await ToSignal(GetTree().CreateTimer(5), SceneTreeTimer.SignalName.Timeout);
        sampling = false;
        SaveFrames(name);
        var sorted = samples.Select(s => s.GapMs).Order().ToArray();
        using var process = Process.GetCurrentProcess();
        var screen = viewports.Single(v => v.Name == "MajoDirectionScreenViewport");
        // Readback is outside the timing window and excludes the dialogue UI.
        using var image = screen.GetTexture().GetImage();
        image.SavePng(Path.Combine(directory, name + ".png"));
        var result = new
        {
            name, frames = samples.Count, fps = 1000 / sorted.Average(), medianMs = sorted[sorted.Length / 2],
            p95Ms = sorted[(int)(sorted.Length * .95)], p99Ms = sorted[(int)(sorted.Length * .99)], maxMs = sorted[^1],
            overBudgetPercent = 100.0 * sorted.Count(v => v > 1000.0 / 120) / sorted.Length,
            focusedPercent = 100.0 * samples.Count(s => s.Focused) / samples.Count,
            drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            videoMiB = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576,
            rssMiB = process.WorkingSet64 / 1048576.0, managedMiB = GC.GetTotalMemory(false) / 1048576.0,
            activeViewports = viewports.Count(v => v.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled),
            viewDemand = renderHost.InspectViewDemand(),
            screenSha256 = Convert.ToHexString(SHA256.HashData(image.GetData())),
        };
        results.Add(result);
        GD.Print("TRIAL00_STRESS_RESULT " + JsonSerializer.Serialize(result));
    }

    private void SaveFrames(string name)
    {
        using var writer = new StreamWriter(Path.Combine(directory, name + ".csv"));
        writer.WriteLine("time_ms,gap_ms,focused");
        foreach (var sample in samples)
            writer.WriteLine(FormattableString.Invariant($"{sample.TimeMs:F3},{sample.GapMs:F3},{sample.Focused}"));
    }

    private sealed record Sample(double TimeMs, double GapMs, bool Focused);
}

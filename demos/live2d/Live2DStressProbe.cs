#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Majo.Demos.Live2D.Actors;
using Majo.Runtime;
using Majo.Runtime.Actors;

namespace Majo.Demos.Live2D;

/// <summary>Opt-in rendered Cubism stress test; normal demo startup is unaffected.</summary>
public partial class Live2DStressProbe : Node
{
    public const int MinimumActorCount = 1;
    private const double WarmupSeconds = 3;
    private const double SampleSeconds = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<FrameSample> samples = [];
    private bool sampling;
    private double previousMs;

    public override void _Process(double delta)
    {
        _ = delta;
        var now = clock.Elapsed.TotalMilliseconds;
        if (sampling && previousMs > 0)
        {
            samples.Add(new FrameSample(
                now - previousMs,
                RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()),
                RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()),
                Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)));
        }
        previousMs = now;
    }

    public async Task RunAsync(MajoRuntimeBootstrap runtime, int actorCount)
    {
        if (actorCount < MinimumActorCount)
            throw new ArgumentOutOfRangeException(nameof(actorCount),
                $"Live2D stress mode requires at least {MinimumActorCount} actors.");

        var actors = runtime.Host!.Services.Get<IActorManager>();
        var registry = runtime.Host.Services.Get<Live2DActorRegistry>();
        var created = new List<Live2DCharacterActor>(actorCount);
        for (var index = 0; index < actorCount; index++)
        {
            var actor = await actors.GetOrCreateAsync(new ActorSpawnRequest(
                $"live2d-stress-{index + 1:00}",
                Live2DActorIds.Factory,
                "neutral",
                requiredFactoryId: Live2DActorIds.Factory)
            {
                DefinitionId = "mao",
                RequireExplicitDefinition = true,
            });
            created.Add((Live2DCharacterActor)actor);
        }

        var stressLayer = new CanvasLayer { Name = "Live2DStressLayer", Layer = 100 };
        var stressRoot = new Node2D { Name = "Live2DStressRoot" };
        AddChild(stressLayer);
        stressLayer.AddChild(stressRoot);
        var viewportSize = GetViewport().GetVisibleRect().Size;
        stressRoot.Scale = new Vector2(viewportSize.X / 1920, viewportSize.Y / 1080);
        foreach (var actor in created)
            actor.SceneNode.Reparent(stressRoot, keepGlobalTransform: false);

        Arrange(created);
        foreach (var actor in created)
            await registry.PlayMotionAsync(actor.Id, "idle", wait: false, default);

        var models = created.Select(actor => actor.SceneNode.GetNode<Node2D>("CubismModel")).ToArray();
        var maskViewportLimit = ParseIntegerArgument("--live2d-stress-mask-size=", minimum: 0);
        if (maskViewportLimit > 0)
            foreach (var model in models)
                model.Call("set_mask_viewport_size", maskViewportLimit);
        var staticMode = Array.IndexOf(OS.GetCmdlineUserArgs(), "--live2d-stress-static") >= 0;

        GetWindow().Title = $"Live2D stress test — {actorCount} actors";
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        await ToSignal(GetTree().CreateTimer(WarmupSeconds), SceneTreeTimer.SignalName.Timeout);
        if (staticMode)
            foreach (var model in models)
                model.Call("set_process_callback", 2);

        var originalVsync = DisplayServer.WindowGetVsyncMode();
        var originalMaxFps = Engine.MaxFps;
        var results = new List<object>();
        var outputDirectory = ProjectSettings.GlobalizePath(
            "res://artifacts/live2d-stress/" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            results.Add(await MeasureAsync("vsync", actorCount));
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            results.Add(await MeasureAsync("uncapped", actorCount));

            using var image = GetViewport().GetTexture().GetImage();
            image.SavePng(Path.Combine(outputDirectory, "live2d-stress.png"));
            var maskViewports = models
                .SelectMany(model => model.FindChildren("*", "SubViewport", true, false))
                .Cast<SubViewport>()
                .ToArray();
            var report = new
            {
                actorCount,
                staticMode,
                maskViewportLimit,
                meshCount = models.Sum(model =>
                    model.FindChildren("*", "MeshInstance2D", true, false).Count),
                maskViewportCount = maskViewports.Length,
                maskMegapixels = maskViewports.Sum(viewport =>
                    (long)viewport.Size.X * viewport.Size.Y) / 1_000_000.0,
                maskSizes = maskViewports
                    .GroupBy(viewport => viewport.Size.ToString())
                    .Select(group => new { size = group.Key, count = group.Count() })
                    .OrderBy(group => group.size)
                    .ToArray(),
                warmupSeconds = WarmupSeconds,
                sampleSeconds = SampleSeconds,
                renderer = RenderingServer.GetCurrentRenderingMethod().ToString(),
                renderingDriver = RenderingServer.GetCurrentRenderingDriverName().ToString(),
                windowSize = GetWindow().Size.ToString(),
                refreshHz = DisplayServer.ScreenGetRefreshRate(),
                results,
            };
            var reportPath = Path.Combine(outputDirectory, "results.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            GD.Print("LIVE2D_STRESS_RESULT " + JsonSerializer.Serialize(report));
            GD.Print($"LIVE2D_STRESS_COMPLETE {outputDirectory}");
        }
        finally
        {
            sampling = false;
            DisplayServer.WindowSetVsyncMode(originalVsync);
            Engine.MaxFps = originalMaxFps;
            await runtime.RequestShutdownAsync();
        }
        GetTree().Quit(0);
    }

    private async Task<object> MeasureAsync(string name, int actorCount)
    {
        sampling = false;
        await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
        samples.Clear();
        previousMs = 0;
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var elapsedStart = clock.Elapsed;
        sampling = true;
        await ToSignal(GetTree().CreateTimer(SampleSeconds), SceneTreeTimer.SignalName.Timeout);
        sampling = false;
        process.Refresh();
        var elapsed = clock.Elapsed - elapsedStart;
        var cpu = process.TotalProcessorTime - cpuStart;
        var frame = samples.Select(sample => sample.FrameMs).Order().ToArray();
        var renderCpu = samples.Select(sample => sample.RenderCpuMs).Order().ToArray();
        var renderGpu = samples.Select(sample => sample.RenderGpuMs).Order().ToArray();
        if (frame.Length == 0)
            throw new InvalidOperationException("Live2D stress test did not collect any rendered frames.");

        var result = new
        {
            name,
            actorCount,
            frames = frame.Length,
            fps = 1000 / frame.Average(),
            frameMs = Percentiles(frame),
            renderCpuMs = Percentiles(renderCpu),
            renderGpuMs = Percentiles(renderGpu),
            over16_67Percent = 100.0 * frame.Count(value => value > 1000.0 / 60) / frame.Length,
            averageDrawCalls = samples.Average(sample => sample.DrawCalls),
            processCpuPercent = 100 * cpu.TotalSeconds / elapsed.TotalSeconds / System.Environment.ProcessorCount,
            rssMiB = process.WorkingSet64 / 1048576.0,
            managedMiB = GC.GetTotalMemory(false) / 1048576.0,
            textureMiB = SafeMemoryMiB(Performance.Monitor.RenderTextureMemUsed),
            videoMiB = SafeMemoryMiB(Performance.Monitor.RenderVideoMemUsed),
            gpuTimingAvailable = renderGpu.Any(value => value > 0),
        };
        GD.Print("LIVE2D_STRESS_PHASE " + JsonSerializer.Serialize(result));
        return result;
    }

    private static object Percentiles(double[] sorted) => new
    {
        average = sorted.Average(),
        median = sorted[sorted.Length / 2],
        p95 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .95))],
        p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99))],
        max = sorted[^1],
    };

    private static double? SafeMemoryMiB(Performance.Monitor monitor)
    {
        var value = Performance.GetMonitor(monitor) / 1048576.0;
        return value is >= 0 and <= 1048576 ? value : null;
    }

    private static int ParseIntegerArgument(string prefix, int minimum)
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(argument[prefix.Length..], out var value) && value >= minimum)
                return value;
            throw new ArgumentException($"{prefix}<value> must be at least {minimum}.");
        }
        return 0;
    }

    private static void Arrange(List<Live2DCharacterActor> actors)
    {
        const float width = 1920;
        const float height = 1080;
        var columns = (int)Math.Ceiling(Math.Sqrt(actors.Count * width / height));
        var rows = (int)Math.Ceiling((double)actors.Count / columns);
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        var scale = Math.Min(cellWidth / 900, cellHeight / 1800) * .92f;
        for (var index = 0; index < actors.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            actors[index].SetScale(Vector2.One * scale);
            actors[index].SetPosition(new Vector2(
                (column + .5f) * cellWidth,
                (row + 1) * cellHeight + 35));
            actors[index].SetZIndex(index);
        }
    }

    private sealed record FrameSample(
        double FrameMs,
        double RenderCpuMs,
        double RenderGpuMs,
        double DrawCalls);
}

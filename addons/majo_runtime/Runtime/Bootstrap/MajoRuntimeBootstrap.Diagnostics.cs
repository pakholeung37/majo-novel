#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Input;
using Majo.Runtime.UI.Diagnostics;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void InitializeLogging()
    {
        var sinks = new List<IRuntimeLogSink>();
        var ringCapacity = Configuration?.Diagnostics?.RingBufferCapacity ?? 500;
        RingBuffer = new RingBufferLogSink(ringCapacity);
        sinks.Add(RingBuffer);
        sinks.Add(new GodotConsoleSink(Configuration?.Diagnostics?.IncludeCorrelation ?? false));

        if (Configuration?.Diagnostics?.EnableFileLogging == true)
        {
            var logDir = NormalizeLogDirectory(Configuration.Diagnostics.FileLogDirectory);
            sinks.Add(new JsonLinesFileSink(
                logDir,
                "majo_runtime",
                Configuration.Diagnostics.MaxFileSizeBytes,
                Configuration.Diagnostics.MaxFileCount));
        }

        var compositeSink = new CompositeRuntimeLogSink(sinks);
        var minLevel = Configuration?.Diagnostics?.MinimumLevel ?? RuntimeLogLevel.Information;
        Logger = new StructuredRuntimeLogger(compositeSink, minLevel);
    }

    private static string NormalizeLogDirectory(string rawDir)
    {
        if (rawDir.StartsWith("user://", StringComparison.Ordinal) ||
            rawDir.StartsWith("res://", StringComparison.Ordinal))
        {
            return ProjectSettings.GlobalizePath(rawDir);
        }

        return rawDir;
    }

    private void InitializeOverlays()
    {
        if (Configuration?.Diagnostics?.EnableOverlay == true &&
            Host?.Services.TryGet<IRuntimeDiagnostics>(out var diagnostics) == true &&
            Host.Services.TryGet<IInputManager>(out var inputManager) == true)
        {
            DebugOverlay = new DefaultDebugOverlay(
                this, diagnostics!, inputManager!, Configuration.Diagnostics);
        }

        var showFps = Configuration?.Diagnostics?.ShowFpsOverlay ?? true;
        if (showFps && OS.IsDebugBuild())
        {
            FpsOverlay = new DefaultFpsOverlay(this, Configuration?.Diagnostics);
        }
    }

    private void TeardownDiagnosticsAndOverlays()
    {
        FpsOverlay?.Dispose();
        FpsOverlay = null;
        DebugOverlay?.Dispose();
        DebugOverlay = null;
        Logger?.Dispose();
        Logger = null;
    }
}

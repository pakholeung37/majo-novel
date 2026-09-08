using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Input;
using Majo.Runtime.Resources;
using Majo.Runtime.UI.Diagnostics;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateDiagnosticsRuntimeAsync()
    {
        using (var image = Image.CreateEmpty(512, 512, false, Image.Format.Rgba8))
        using (var texture = ImageTexture.CreateFromImage(image))
        using (var atlas = new AtlasTexture { Atlas = texture })
        using (var wav = new AudioStreamWav { Data = new byte[1048576] })
        {
            var sampler = new ResourceMemorySampler();
            var summary = sampler.Sample(new ResourceDiagnosticEntry[]
            {
                new(texture, 1, 0), new(texture, 1, 1), new(atlas, 0, 0),
                new(wav, 1, 0), new(new object(), 0, 0),
            });
            Assert(summary.Contains("Tex ≈   1M", StringComparison.Ordinal), "Shared atlas texture must only count once.");
            Assert(summary.Contains("Aud ≈  1M", StringComparison.Ordinal), "Audio should count payload bytes.");
            Assert(summary.Contains("Res   4 (P 1/L 1/C 2) · ?1", StringComparison.Ordinal), "Resource identity and ownership buckets must be deduplicated.");
            var empty = sampler.Sample(Array.Empty<ResourceDiagnosticEntry>());
            Assert(!empty.Contains('?'), "Empty cache must not retain previously sampled resources.");
        }
        var configuration = CreateHeadlessConfiguration();
        Assert(!new DiagnosticsConfiguration().IncludeCorrelation, "DiagnosticsConfiguration.IncludeCorrelation should default to false.");
        configuration.Diagnostics = new DiagnosticsConfiguration
        {
            MinimumLevel = RuntimeLogLevel.Debug,
            RingBufferCapacity = 200,
            EnableOverlay = true,
            defaultOverlayVisible = true,
            EnableFileLogging = true,
            FileLogDirectory = "user://test_logs",
        };

        var bootstrap = new MajoRuntimeBootstrap
        {
            Configuration = configuration,
        };

        AddChild(bootstrap);
        try
        {
            await bootstrap.InitializationCompletion;
            Assert(bootstrap.IsInitialized, "Bootstrap should be initialized.");
            Assert(bootstrap.Diagnostics is not null, "Diagnostics service should be available.");
            Assert(bootstrap.DebugOverlay is not null, "Debug overlay should be instantiated.");
            Assert(bootstrap.FpsOverlay is not null, "FPS overlay should be instantiated in debug mode.");

            // Verify the live overlay receives FPS and memory data.
            var fpsOverlay = bootstrap.FpsOverlay;
            var fpsLabel = fpsOverlay.FpsLabel;
            Assert(fpsLabel is not null, "FPS label must exist.");
            Assert(fpsLabel.Text.StartsWith("FPS:", StringComparison.Ordinal), "FPS label should display FPS prefix.");

            var memoryLabel = fpsOverlay.FindChild("MemoryLabel", recursive: true, owned: false) as Label;
            Assert(memoryLabel is not null && memoryLabel.Text.Contains("RAM", StringComparison.Ordinal)
                && memoryLabel.Text.Contains("RM Tex", StringComparison.Ordinal), "Overlay should display process and resource memory.");

            // Verify lifecycle log events were captured in ring buffer
            var recentEvents = bootstrap.Diagnostics.RecentEvents;
            Assert(recentEvents.Count > 0, "Recent log events should not be empty.");
            Assert(recentEvents.Any(e => e.EventId == "core.lifecycle.initialize_started"), "Should log host initializing.");
            Assert(recentEvents.Any(e => e.EventId == "core.lifecycle.initialize_completed"), "Should log host initialized.");

            // Verify correlation and runtime ID consistency
            var hostInitEvent = recentEvents.First(e => e.EventId == "core.lifecycle.initialize_completed");
            Assert(!string.IsNullOrEmpty(hostInitEvent.Correlation.RuntimeId), "Host init event must have RuntimeId in correlation.");
            Assert(hostInitEvent.Correlation.RuntimeId == bootstrap.Host!.RuntimeId, "Correlation RuntimeId must match Host RuntimeId.");

            // Verify snapshot capture
            var snapshot = bootstrap.Diagnostics.CaptureSnapshot();
            Assert(snapshot.RuntimeId == bootstrap.Host.RuntimeId, "Snapshot RuntimeId must match Host.");
            Assert(snapshot.State == RuntimeHostState.Initialized, "State in snapshot must be Initialized.");
            Assert(snapshot.RecentLogs is not null && snapshot.RecentLogs.Count > 0, "Snapshot must include recent logs.");
            Assert(snapshot.Dialogue is not null, "Snapshot must include dialogue diagnostics.");
            Assert(snapshot.Input is not null, "Snapshot must include input diagnostics.");

            // Verify debug overlay toggle and input isolation
            var overlay = bootstrap.DebugOverlay;
            var input = bootstrap.Host.Services.Get<IInputManager>();
            var openHandleCount = input.ActiveHandleCount;
            Assert(overlay.IsOpen, "A default-visible debug overlay should be open and refreshed initially.");

            var levelFilter = overlay.FindChild("LogLevelFilter", recursive: true, owned: false) as OptionButton;
            var logText = overlay.FindChild("RecentLogEvents", recursive: true, owned: false) as RichTextLabel;
            var overlayRoot = overlay.FindChild("MajoDebugOverlayRoot", recursive: true, owned: false) as Control;
            Assert(overlayRoot is not null && Mathf.IsEqualApprox(overlayRoot.AnchorBottom, 0.75f),
                "Debug overlay should reserve the bottom quarter of the viewport for the line presenter.");
            Assert(levelFilter is not null && levelFilter.ItemCount == 5,
                "Debug overlay should provide All, Debug, Information, Warning, and Error filters.");
            Assert(logText is not null && logText.BbcodeEnabled,
                "Recent log events should support colored BBCode output.");

            bootstrap.Logger!.Debug("test", "test.debug", "Debug filter probe");
            bootstrap.Logger.Info("test", "test.info", "Information filter probe");
            bootstrap.Logger.Warning("test", "test.warning", "Warning filter probe");
            bootstrap.Logger.Error("test", "test.error", "Error filter probe");
            overlay.Refresh();
            var allLevelsText = logText!.Text;
            Assert(allLevelsText.Contains("Debug filter probe") &&
                allLevelsText.Contains("Information filter probe") &&
                allLevelsText.Contains("Warning filter probe") &&
                allLevelsText.Contains("Error filter probe"),
                "All Levels should show events from every log level.");
            Assert(!allLevelsText.Contains("Correlation:"),
                "The compact overlay should hide correlation details.");

            levelFilter!.EmitSignal(OptionButton.SignalName.ItemSelected, 4);
            var errorsOnlyText = logText.Text;
            Assert(errorsOnlyText.Contains("Error filter probe") &&
                !errorsOnlyText.Contains("Debug filter probe") &&
                !errorsOnlyText.Contains("Information filter probe") &&
                !errorsOnlyText.Contains("Warning filter probe"),
                "Error filter should show only Error events.");

            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = Key.F3,
                Pressed = true,
            });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(!overlay.IsOpen, "Debug overlay should close through its modal F3 input scope.");
            Assert(input.ActiveHandleCount == openHandleCount - 1, "Closing overlay should release its modal scope.");

            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = Key.F3,
                Pressed = false,
            });
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = Key.F3,
                Pressed = true,
            });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(overlay.IsOpen, "Debug overlay should reopen through the real F3 input path.");
            Assert(input.ActiveHandleCount == openHandleCount, "Reopening overlay should reacquire one modal scope.");

            // Clean shutdown
            await bootstrap.RequestShutdownAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(!bootstrap.IsInitialized, "Bootstrap should no longer be initialized after shutdown.");
            Assert(bootstrap.DebugOverlay is null, "Debug overlay should be cleaned up on shutdown.");
            Assert(bootstrap.FpsOverlay is null, "FPS overlay should be cleaned up on shutdown.");

        }
        finally
        {
            if (IsInstanceValid(bootstrap) && bootstrap.IsInsideTree())
            {
                bootstrap.QueueFree();
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Input;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateDiagnosticsRuntimeAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
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

            // Verify FPS overlay layout and styling
            var fpsOverlay = bootstrap.FpsOverlay;
            var fpsRoot = fpsOverlay.FindChild("MajoFpsOverlayRoot", recursive: true, owned: false) as Control;
            Assert(fpsRoot is not null, "FPS overlay root must exist.");
            Assert(Mathf.IsZeroApprox(fpsRoot.AnchorLeft) && Mathf.IsZeroApprox(fpsRoot.AnchorTop),
                "FPS overlay root should be anchored at top-left.");
            var fpsLabel = fpsOverlay.FpsLabel;
            Assert(fpsLabel is not null, "FPS label must exist.");
            Assert(fpsLabel.Text.StartsWith("FPS:", StringComparison.Ordinal), "FPS label should display FPS prefix.");

            var fontColor = fpsLabel.GetThemeColor("font_color");
            Assert(Mathf.IsEqualApprox(fontColor.G, 1.0f) && Mathf.IsZeroApprox(fontColor.R) && Mathf.IsZeroApprox(fontColor.B),
                "FPS label text must be green.");


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

            // Verify markdown diagnostic report export
            var exportPath = Path.Combine(OS.GetUserDataDir(), "test_report.md");
            var reportText = await bootstrap.Diagnostics.ExportReportAsync(exportPath);
            Assert(reportText.Contains("# Majo Runtime Diagnostic Report"), "Report must contain title header.");
            Assert(reportText.Contains(bootstrap.Host.RuntimeId), "Report must include RuntimeId.");
            Assert(File.Exists(exportPath), "Exported report file must exist on disk.");

            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }

            // Verify debug overlay toggle and input isolation
            var overlay = bootstrap.DebugOverlay;
            var input = bootstrap.Host.Services.Get<IInputManager>();
            var openHandleCount = input.ActiveHandleCount;
            Assert(overlay.IsOpen, "A default-visible debug overlay should be open and refreshed initially.");

            var levelFilter = overlay.FindChild("LogLevelFilter", recursive: true, owned: false) as OptionButton;
            var logText = overlay.FindChild("RecentLogEvents", recursive: true, owned: false) as RichTextLabel;
            var summaryGrid = overlay.FindChild("DiagnosticsSummaryGrid", recursive: true, owned: false) as GridContainer;
            var overlayRoot = overlay.FindChild("MajoDebugOverlayRoot", recursive: true, owned: false) as Control;
            Assert(overlayRoot is not null && Mathf.IsEqualApprox(overlayRoot.AnchorBottom, 0.75f),
                "Debug overlay should reserve the bottom quarter of the viewport for the line presenter.");
            Assert(summaryGrid is not null && summaryGrid.Columns == 3,
                "Debug overlay summary should use a compact three-column layout.");
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
            Assert(allLevelsText.Contains("#7ee787") &&
                allLevelsText.Contains("#58a6ff") &&
                allLevelsText.Contains("#d29922") &&
                allLevelsText.Contains("#f85149"),
                "Every log level should have a distinct display color.");

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

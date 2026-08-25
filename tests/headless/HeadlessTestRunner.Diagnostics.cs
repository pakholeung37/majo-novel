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
		configuration.Diagnostics = new DiagnosticsConfiguration
		{
			MinimumLevel = RuntimeLogLevel.Debug,
			RingBufferCapacity = 200,
			EnableOverlay = true,
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
			Assert(bootstrap.RingBuffer is not null, "Ring buffer should be available.");
			Assert(bootstrap.DebugOverlay is not null, "Debug overlay should be instantiated.");

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
			var closedHandleCount = input.ActiveHandleCount;
			Assert(!overlay.IsOpen, "Debug overlay should be closed initially.");

			Godot.Input.ParseInputEvent(new InputEventKey
			{
				PhysicalKeycode = Key.F3,
				Pressed = true,
			});
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Assert(overlay.IsOpen, "Debug overlay should open through the real F3 input path.");
			Assert(input.ActiveHandleCount == closedHandleCount + 1, "Open overlay should own exactly one modal scope.");

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
			Assert(!overlay.IsOpen, "Debug overlay should close through its modal F3 input scope.");
			Assert(input.ActiveHandleCount == closedHandleCount, "Closing overlay should release its modal scope.");

			// Clean shutdown
			await bootstrap.RequestShutdownAsync();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Assert(!bootstrap.IsInitialized, "Bootstrap should no longer be initialized after shutdown.");
			Assert(bootstrap.DebugOverlay is null, "Debug overlay should be cleaned up on shutdown.");
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

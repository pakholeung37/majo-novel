#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DiagnosticsLoggingTests
{
    [Fact]
    public void StructuredLogger_FiltersByMinimumLevel()
    {
        var recording = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(recording, RuntimeLogLevel.Information);

        logger.Debug("test", "test.debug", "Debug message");
        logger.Info("test", "test.info", "Info message");
        logger.Warning("test", "test.warning", "Warning message");

        Assert.Equal(2, recording.Events.Count);
        Assert.Equal(RuntimeLogLevel.Information, recording.Events[0].Level);
        Assert.Equal(RuntimeLogLevel.Warning, recording.Events[1].Level);
    }

    [Fact]
    public void StructuredLogger_AppliesCategoryOverrides_AndHierarchicalPrefix()
    {
        var recording = new RecordingLogSink();
        var overrides = new Dictionary<string, RuntimeLogLevel>
        {
            ["dialogue"] = RuntimeLogLevel.Debug,
            ["core"] = RuntimeLogLevel.Warning,
        };
        var logger = new StructuredRuntimeLogger(recording, RuntimeLogLevel.Information, overrides);

        logger.Debug("dialogue", "dialogue.line", "Dialogue line");
        logger.Debug("operation", "operation.start", "Operation start");
        logger.Info("core.lifecycle", "core.lifecycle.init", "Core init");
        logger.Warning("core.lifecycle", "core.lifecycle.warn", "Core warn");

        Assert.Equal(2, recording.Events.Count);
        Assert.Equal("dialogue.line", recording.Events[0].EventId);
        Assert.Equal("core.lifecycle.warn", recording.Events[1].EventId);
    }

    [Fact]
    public void RuntimeLogCorrelation_MaintainsCallChain()
    {
        var root = RuntimeLogCorrelation.CreateRoot("test-runtime");
        Assert.Equal("test-runtime", root.RuntimeId);
        Assert.Null(root.SessionId);

        var sessionScope = root.WithSessionId("session-1");
        var dialogueScope = sessionScope.WithDialogueSessionId("proj:start");
        var commandScope = dialogueScope.WithCommandId("actor.move");
        var opScope = commandScope.WithOperationId("op-42");

        Assert.Equal("test-runtime", opScope.RuntimeId);
        Assert.Equal("session-1", opScope.SessionId);
        Assert.Equal("proj:start", opScope.DialogueSessionId);
        Assert.Equal("actor.move", opScope.CommandId);
        Assert.Equal("op-42", opScope.OperationId);
    }

    [Fact]
    public void StructuredLogger_InheritsAmbientCorrelationAcrossChildEvents()
    {
        var recording = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(recording, RuntimeLogLevel.Debug);
        logger.SetRootCorrelation(new RuntimeLogCorrelation("runtime-1", "session-1"));

        using (logger.BeginScope(new RuntimeLogCorrelation(
                   string.Empty,
                   dialogueSessionId: "dialogue-1",
                   commandId: "command-1")))
        {
            logger.Debug(
                "operation",
                "operation.started",
                "Started",
                new RuntimeLogCorrelation(string.Empty, operationId: "operation-1"));
        }

        var correlation = Assert.Single(recording.Events).Correlation;
        Assert.Equal("runtime-1", correlation.RuntimeId);
        Assert.Equal("session-1", correlation.SessionId);
        Assert.Equal("dialogue-1", correlation.DialogueSessionId);
        Assert.Equal("command-1", correlation.CommandId);
        Assert.Equal("operation-1", correlation.OperationId);
    }

    [Fact]
    public async Task MissingResourceReporterDeduplicatesLogsAndTracksUseCount()
    {
        var recording = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(recording, RuntimeLogLevel.Debug);
        var reporter = new MissingResourceReporter(logger);

        reporter.Report("Bgm", "demo-missing", "skip", "audio/bgm/demo-missing");
        reporter.Report("Bgm", "demo-missing", "skip", "audio/bgm/demo-missing");

        var occurrence = Assert.Single(reporter.Occurrences);
        Assert.Equal(2, occurrence.Count);
        var loggedEvent = Assert.Single(
            recording.Events,
            entry => entry.EventId == "resource.missing_fallback");
        Assert.Equal("Missing Bgm resource 'audio/bgm/demo-missing'; fallback 'skip' was used.", loggedEvent.Message);

        await reporter.ResetAsync(CancellationToken.None);
        Assert.Empty(reporter.Occurrences);
    }

    [Fact]
    public void RingBufferSink_EvictsOldestEntries_WhenCapacityExceeded()
    {
        var sink = new RingBufferLogSink(capacity: 3);

        sink.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "test", "event.1", "Msg 1"));
        sink.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "test", "event.2", "Msg 2"));
        sink.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "test", "event.3", "Msg 3"));
        sink.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "test", "event.4", "Msg 4"));
        sink.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "test", "event.5", "Msg 5"));

        var recent = sink.RecentEvents;
        Assert.Equal(3, recent.Count);
        Assert.Equal("event.3", recent[0].EventId);
        Assert.Equal("event.4", recent[1].EventId);
        Assert.Equal("event.5", recent[2].EventId);

        sink.Clear();
        Assert.Empty(sink.RecentEvents);
    }

    [Fact]
    public void CompositeSink_IsolatesFaultySink_AndDeliversToOthers()
    {
        var faulty = new FaultyLogSink();
        var recording = new RecordingLogSink();
        var composite = new CompositeRuntimeLogSink([faulty, recording]);

        var logEvent = new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Error, "test", "test.err", "Failed");
        composite.Emit(logEvent);

        Assert.Single(recording.Events);
        Assert.Equal("test.err", recording.Events[0].EventId);
    }

    [Fact]
    public async Task JsonLinesFileSink_WritesAndRotates_Correctly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "majo_tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var sink = new JsonLinesFileSink(tempDir, "test_log", maxFileSizeBytes: 300, maxFileCount: 3))
            {
                for (var i = 0; i < 15; i++)
                {
                    sink.Emit(new RuntimeLogEvent(
                        DateTimeOffset.UtcNow,
                        RuntimeLogLevel.Information,
                        "test.category",
                        $"event.{i}",
                        $"Longer message string for payload testing number {i} with metadata",
                        null,
                        new RuntimeLogCorrelation("rt-1", "s-1", "d-1", "c-1", "o-1"),
                        new Dictionary<string, object?> { ["index"] = i }));
                }

                sink.Flush();
            }

            var files = Directory.GetFiles(tempDir, "test_log*.jsonl");
            Assert.NotEmpty(files);

            var allLines = files.SelectMany(File.ReadAllLines).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.NotEmpty(allLines);

            var firstDoc = JsonDocument.Parse(allLines[0]);
            var root = firstDoc.RootElement;
            Assert.Equal("test.category", root.GetProperty("Category").GetString());
            Assert.Equal("Information", root.GetProperty("Level").GetString());
            Assert.Equal("rt-1", root.GetProperty("Correlation").GetProperty("RuntimeId").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DiagnosticsDTOs_ArePureDotNet_WithoutGodotOrYarnReferences()
    {
        var diagnosticTypes = new[]
        {
            typeof(RuntimeLogEvent),
            typeof(RuntimeLogCorrelation),
            typeof(RuntimeLogLevel),
            typeof(RuntimeDiagnosticsSnapshot),
            typeof(DialogueDiagnostics),
            typeof(CommandDiagnostics),
            typeof(ActiveCommandInfo),
            typeof(OperationDiagnostics),
            typeof(ActiveOperationInfo),
            typeof(InputDiagnostics),
            typeof(InputScopeInfo),
            typeof(ActorDiagnostics),
            typeof(ActiveActorInfo),
            typeof(AudioDiagnostics),
            typeof(BgmTrackInfo),
            typeof(VoicePlaybackInfo),
            typeof(StateDiagnostics),
            typeof(PlaybackDiagnostics),
            typeof(MissingResourceDiagnostics),
            typeof(MissingResourceInfo),
            typeof(DiagnosticSection),
            typeof(IDiagnosticContributor),
            typeof(IRuntimeDiagnostics),
        };

        foreach (var type in diagnosticTypes)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                var propType = property.PropertyType;
                AssertDoesNotReferenceGodotOrYarn(propType, $"{type.Name}.{property.Name}");
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                AssertDoesNotReferenceGodotOrYarn(fieldType, $"{type.Name}.{field.Name}");
            }
        }
    }

    [Fact]
    public async Task RuntimeDiagnosticsService_CapturesSnapshotAndExportsReport()
    {
        var services = new RuntimeServiceRegistry();
        var ringBuffer = new RingBufferLogSink(100);
        ringBuffer.Emit(new RuntimeLogEvent(DateTimeOffset.UtcNow, RuntimeLogLevel.Information, "core.lifecycle", "init", "Initialized"));

        var contributor = new TestDiagnosticContributor("custom_test", new DiagnosticSection("Custom", new Dictionary<string, object?> { ["version"] = 1 }));
        var registry = new DiagnosticContributorRegistry().Register(contributor);

        var diagnosticsService = new RuntimeDiagnosticsService(
            new TestServiceResolver(),
            ringBuffer,
            registry,
            "test-runtime-id",
            "1.0.0");

        var snapshot = diagnosticsService.CaptureSnapshot();

        Assert.Equal("test-runtime-id", snapshot.RuntimeId);
        Assert.Equal("1.0.0", snapshot.RuntimeVersion);
        Assert.Single(snapshot.RecentLogs!);
        Assert.True(snapshot.CustomSections?.ContainsKey("custom_test"));

        var report = await diagnosticsService.ExportReportAsync();
        Assert.Contains("# Majo Runtime Diagnostic Report", report);
        Assert.Contains("test-runtime-id", report);
        Assert.Contains("custom_test", report);
        Assert.Contains("Recent Log Events", report);
    }

    private static void AssertDoesNotReferenceGodotOrYarn(Type type, string location)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (unwrapped.IsGenericType)
        {
            foreach (var arg in unwrapped.GetGenericArguments())
            {
                AssertDoesNotReferenceGodotOrYarn(arg, location);
            }
            return;
        }

        if (unwrapped.IsArray)
        {
            AssertDoesNotReferenceGodotOrYarn(unwrapped.GetElementType()!, location);
            return;
        }

        var assemblyName = unwrapped.Assembly.GetName().Name ?? string.Empty;
        Assert.False(
            assemblyName.StartsWith("Godot", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("Yarn", StringComparison.OrdinalIgnoreCase),
            $"{location} has type '{unwrapped.FullName}' referencing assembly '{assemblyName}'.");
    }

    private sealed class FaultyLogSink : IRuntimeLogSink
    {
        public void Emit(RuntimeLogEvent logEvent) =>
            throw new InvalidOperationException("Simulated sink explosion");

        public void Dispose()
        {
        }
    }

    private sealed class TestDiagnosticContributor : IDiagnosticContributor
    {
        private readonly DiagnosticSection section;

        public TestDiagnosticContributor(string key, DiagnosticSection section)
        {
            Key = key;
            this.section = section;
        }

        public string Key { get; }

        public DiagnosticSection Capture() => section;
    }

    private sealed class TestServiceResolver : IServiceResolver
    {
        public TService Get<TService>() where TService : class =>
            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' not registered.");

        public object Get(Type serviceType) =>
            throw new InvalidOperationException($"Service '{serviceType.FullName}' not registered.");

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            service = null;
            return false;
        }
    }
}

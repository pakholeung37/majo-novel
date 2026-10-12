#nullable enable

using System;
using System.Linq;
using System.Text;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;

namespace Majo.Runtime.UI.Diagnostics;

public partial class DefaultDebugOverlay : CanvasLayer, IDisposable
{
    private readonly Node hostNode;
    private readonly IRuntimeDiagnostics diagnostics;
    private readonly IInputManager input;
    private readonly DiagnosticsConfiguration configuration;
    private IInputHandle? toggleHandle;
    private IInputHandle? modalHandle;
    private Control? rootControl;
    private Label? titleLabel;
    private Button? closeButton;
    private Button? refreshButton;
    private Button? copySummaryButton;
    private Button? exportButton;
    private Label? runtimeSummaryLabel;
    private Label? dialogueSummaryLabel;
    private Label? activitySummaryLabel;
    private Label? logHeaderLabel;
    private OptionButton? logLevelFilter;
    private RichTextLabel? logTextLabel;
    private Label? statusLabel;
    private InputActionId? toggleAction;
    private bool isOpen;
    private bool isDisposed;
    private RuntimeLogLevel? selectedLogLevel;
    private double refreshElapsedSeconds;

    private const double RefreshIntervalSeconds = 0.5;

    public DefaultDebugOverlay(
        Node hostNode,
        IRuntimeDiagnostics diagnostics,
        IInputManager input,
        DiagnosticsConfiguration? configuration = null)
    {
        this.hostNode = hostNode ?? throw new ArgumentNullException(nameof(hostNode));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.configuration = configuration ?? new DiagnosticsConfiguration();
        Layer = 120;
        BuildUi();
        hostNode.AddChild(this);
        RegisterToggleInput();
        if (this.configuration.defaultOverlayVisible)
        {
            Open();
        }
    }

    public bool IsOpen => isOpen;

    public override void _Process(double delta)
    {
        if (!isOpen || isDisposed)
        {
            return;
        }

        refreshElapsedSeconds += delta;
        if (refreshElapsedSeconds >= RefreshIntervalSeconds)
        {
            refreshElapsedSeconds = 0;
            Refresh();
        }
    }

    public void Open()
    {
        if (isOpen || isDisposed)
        {
            return;
        }

        isOpen = true;
        refreshElapsedSeconds = 0;
        rootControl!.Visible = true;
        if (modalHandle is null)
        {
            modalHandle = input.AcquireScope(
                this,
                new InputScopeOptions(
                    [toggleAction ?? MajoInputActions.Debug],
                    isModal: false,
                    name: "debug-overlay-scope"));
            modalHandle.Activated += HandleToggleActivated;
        }
        Refresh();
    }

    public void Close()
    {
        if (!isOpen || isDisposed)
        {
            return;
        }

        isOpen = false;
        rootControl!.Visible = false;
        if (modalHandle is not null)
        {
            modalHandle.Activated -= HandleToggleActivated;
            modalHandle.Dispose();
        }
        modalHandle = null;
    }

    public void Toggle()
    {
        if (isOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Refresh()
    {
        if (!isOpen || isDisposed)
        {
            return;
        }

        var snapshot = diagnostics.CaptureSnapshot();
        if (runtimeSummaryLabel is not null)
        {
            runtimeSummaryLabel.Text =
                $"RUNTIME  {snapshot.State}\n" +
                $"{ShortenIdentifier(snapshot.RuntimeId)}  ·  v{snapshot.RuntimeVersion}";
            runtimeSummaryLabel.TooltipText = $"Runtime ID: {snapshot.RuntimeId}";
        }

        if (dialogueSummaryLabel is not null)
        {
            dialogueSummaryLabel.Text =
                $"DIALOGUE  {snapshot.Dialogue?.State.ToString() ?? "N/A"}\n" +
                $"{snapshot.Dialogue?.ProjectId ?? "-"}  ·  Line {snapshot.Dialogue?.LineId ?? "-"}";
        }

        if (activitySummaryLabel is not null)
        {
            activitySummaryLabel.Text =
                $"ACTIVITIES {snapshot.Activities?.ActiveCount ?? 0}  ·  CMDS {snapshot.Command?.ActiveCount ?? 0}  ·  MISSING {snapshot.MissingResources?.UniqueCount ?? 0}/{snapshot.MissingResources?.TotalCount ?? 0}\n" +
                $"{snapshot.Playback?.Mode ?? "Manual"}  ·  History {snapshot.Playback?.HistoryItemCount ?? 0}  ·  State {snapshot.StateDiagnostics?.Status.ToString() ?? "Idle"}";
        }

        if (logTextLabel is not null)
        {
            var logs = snapshot.RecentLogs?
                .Where(log => selectedLogLevel is null || log.Level == selectedLogLevel)
                .ToArray();
            if (logs is null || logs.Length == 0)
            {
                logTextLabel.Text = selectedLogLevel is null
                    ? "No recent log events."
                    : $"No recent {selectedLogLevel} log events.";
            }
            else
            {
                var sb = new StringBuilder();
                for (var i = Math.Max(0, logs.Length - 50); i < logs.Length; i++)
                {
                    var log = logs[i];
                    sb.Append("[color=")
                        .Append(GetLogLevelColor(log.Level))
                        .Append(']')
                        .Append(EscapeBbcode(GodotConsoleSink.FormatEvent(
                            log,
                            includeCorrelation: configuration.IncludeCorrelation)))
                        .AppendLine("[/color]");
                }
                logTextLabel.Text = sb.ToString();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !isDisposed)
        {
            Close();
            isDisposed = true;
            if (toggleHandle is not null)
            {
                toggleHandle.Activated -= HandleToggleActivated;
                toggleHandle.Dispose();
                toggleHandle = null;
            }

            if (logLevelFilter is not null && GodotObject.IsInstanceValid(logLevelFilter))
            {
                logLevelFilter.ItemSelected -= HandleLogLevelSelected;
            }

            if (IsInsideTree())
            {
                QueueFree();
            }
        }

        base.Dispose(disposing);
    }

    private void RegisterToggleInput()
    {
        var configuredAction = string.IsNullOrWhiteSpace(configuration.OverlayAction)
            ? "majo_debug"
            : configuration.OverlayAction.Trim();
        if (configuredAction.StartsWith("majo_", StringComparison.Ordinal))
        {
            configuredAction = configuredAction["majo_".Length..];
        }

        toggleAction = new InputActionId(configuredAction);

        toggleHandle = input.AcquireScope(
            this,
            new InputScopeOptions([toggleAction], name: "debug-overlay-toggle"));
        toggleHandle.Activated += HandleToggleActivated;
    }

    private void HandleToggleActivated(InputActivation activation) => Toggle();

    private void BuildUi()
    {
        rootControl = new Control
        {
            Name = "MajoDebugOverlayRoot",
            AnchorRight = 1.0f,
            AnchorBottom = 0.75f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(rootControl);

        var margin = new MarginContainer
        {
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        rootControl.AddChild(margin);

        var vbox = new VBoxContainer
        {
            Name = "DebugOverlayVBox",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        margin.AddChild(vbox);

        var headerBox = new HBoxContainer
        {
            Name = "HeaderBox",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        vbox.AddChild(headerBox);

        titleLabel = new Label
        {
            Text = TrText(UiStringKeys.DebugTitle, "Majo Runtime Diagnostics Inspector"),
        };
        headerBox.AddChild(titleLabel);

        closeButton = new Button
        {
            Text = TrText(UiStringKeys.DebugClose, " Close (F3/Toggle) "),
        };
        closeButton.Pressed += Close;
        headerBox.AddChild(closeButton);

        refreshButton = new Button
        {
            Text = TrText(UiStringKeys.DebugRefresh, " Refresh "),
        };
        refreshButton.Pressed += Refresh;
        headerBox.AddChild(refreshButton);

        copySummaryButton = new Button
        {
            Text = TrText(UiStringKeys.DebugCopy, " Copy Summary "),
        };
        copySummaryButton.Pressed += HandleCopySummary;
        headerBox.AddChild(copySummaryButton);

        exportButton = new Button
        {
            Text = TrText(UiStringKeys.DebugExport, " Export Report "),
        };
        exportButton.Pressed += HandleExportReport;
        headerBox.AddChild(exportButton);

        statusLabel = new Label
        {
            Text = string.Empty,
        };
        vbox.AddChild(statusLabel);

        var summaryGrid = new GridContainer
        {
            Name = "DiagnosticsSummaryGrid",
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        summaryGrid.AddThemeConstantOverride("h_separation", 4);
        vbox.AddChild(summaryGrid);

        runtimeSummaryLabel = AddSummaryCard(summaryGrid, "Loading runtime...");
        dialogueSummaryLabel = AddSummaryCard(summaryGrid, "Loading dialogue...");
        activitySummaryLabel = AddSummaryCard(summaryGrid, "Loading activity...");

        var logHeaderBox = new HBoxContainer
        {
            Name = "LogHeaderBox",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        vbox.AddChild(logHeaderBox);

        logHeaderLabel = new Label
        {
            Text = TrText(UiStringKeys.DebugRecentLogs, "Recent Log Events:"),
        };
        logHeaderBox.AddChild(logHeaderLabel);

        var logLevelLabel = new Label
        {
            Text = " Level: ",
        };
        logHeaderBox.AddChild(logLevelLabel);

        logLevelFilter = new OptionButton
        {
            Name = "LogLevelFilter",
            TooltipText = "Show all recent events or only events at one log level.",
        };
        logLevelFilter.AddItem("All Levels");
        logLevelFilter.AddItem("Debug");
        logLevelFilter.AddItem("Information");
        logLevelFilter.AddItem("Warning");
        logLevelFilter.AddItem("Error");
        logLevelFilter.ItemSelected += HandleLogLevelSelected;
        logHeaderBox.AddChild(logLevelFilter);

        logTextLabel = new RichTextLabel
        {
            Name = "RecentLogEvents",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollFollowing = true,
            BbcodeEnabled = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        logTextLabel.AddThemeStyleboxOverride("normal", CreatePaddingStyle(
            horizontalPadding: 0,
            verticalPadding: 0));
        logTextLabel.AddThemeConstantOverride("line_separation", 2);
        vbox.AddChild(logTextLabel);
    }

    private static Label AddSummaryCard(GridContainer grid, string initialText)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", CreatePaddingStyle(
            horizontalPadding: 6,
            verticalPadding: 2));
        grid.AddChild(panel);

        var label = new Label
        {
            Text = initialText,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        panel.AddChild(label);
        return label;
    }

    private static StyleBoxFlat CreatePaddingStyle(
        float horizontalPadding,
        float verticalPadding) => new()
        {
            BgColor = Colors.Transparent,
            ContentMarginLeft = horizontalPadding,
            ContentMarginRight = horizontalPadding,
            ContentMarginTop = verticalPadding,
            ContentMarginBottom = verticalPadding,
        };

    private static string ShortenIdentifier(string identifier) =>
        identifier.Length <= 12 ? identifier : $"{identifier[..12]}…";

    private void HandleLogLevelSelected(long index)
    {
        selectedLogLevel = index switch
        {
            1 => RuntimeLogLevel.Debug,
            2 => RuntimeLogLevel.Information,
            3 => RuntimeLogLevel.Warning,
            4 => RuntimeLogLevel.Error,
            _ => null,
        };
        Refresh();
    }

    private static string GetLogLevelColor(RuntimeLogLevel level) => level switch
    {
        RuntimeLogLevel.Debug => "#7ee787",
        RuntimeLogLevel.Information => "#58a6ff",
        RuntimeLogLevel.Warning => "#d29922",
        RuntimeLogLevel.Error => "#f85149",
        _ => "#ffffff",
    };

    private static string EscapeBbcode(string text) =>
        text.Replace("[", "[lb]", StringComparison.Ordinal);

    private static string TrText(string key, string fallback) => fallback;

    private void HandleCopySummary()
    {
        var snapshot = diagnostics.CaptureSnapshot();
        var report = RuntimeDiagnosticsService.FormatReport(snapshot);
        DisplayServer.ClipboardSet(report);
        if (statusLabel is not null)
        {
            statusLabel.Text = "Diagnostic summary copied to clipboard.";
        }
    }

    private async void HandleExportReport()
    {
        try
        {
            var fileName = $"diagnostic_report_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.md";
            var directory = ProjectSettings.GlobalizePath("user://diagnostics");
            var fullPath = System.IO.Path.Combine(directory, fileName);
            await diagnostics.ExportReportAsync(fullPath);
            if (statusLabel is not null)
            {
                statusLabel.Text = $"Exported diagnostic report to {fullPath}";
            }
        }
        catch (Exception exception)
        {
            if (statusLabel is not null)
            {
                statusLabel.Text = $"Export failed: {exception.Message}";
            }
        }
    }
}

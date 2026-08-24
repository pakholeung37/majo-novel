#nullable enable

using System;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Input;

namespace Majo.Runtime.Diagnostics.Godot;

public partial class DefaultDebugOverlay : CanvasLayer, IDisposable
{
    private readonly Node hostNode;
    private readonly IRuntimeDiagnostics diagnostics;
    private readonly IInputManager input;
    private readonly DiagnosticsConfiguration configuration;
    private IInputHandle? toggleHandle;
    private IInputHandle? modalHandle;
    private Control? rootControl;
    private Label? summaryLabel;
    private RichTextLabel? logTextLabel;
    private Label? statusLabel;
    private InputActionId? toggleAction;
    private bool isOpen;
    private bool isDisposed;

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
    }

    public bool IsOpen => isOpen;

    public void Open()
    {
        if (isOpen || isDisposed)
        {
            return;
        }

        isOpen = true;
        rootControl!.Visible = true;
        if (modalHandle is null)
        {
            modalHandle = input.AcquireScope(
                this,
                new InputScopeOptions(
                    [toggleAction ?? MajoInputActions.Debug],
                    isModal: true,
                    name: "debug-overlay-modal"));
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
        if (summaryLabel is not null)
        {
            summaryLabel.Text =
                $"Runtime ID: {snapshot.RuntimeId}\n" +
                $"Host State: {snapshot.State} | Version: {snapshot.RuntimeVersion}\n" +
                $"Dialogue: {snapshot.Dialogue?.State.ToString() ?? "N/A"} (Project: {snapshot.Dialogue?.ProjectId ?? "-"}, Line: {snapshot.Dialogue?.LineId ?? "-"})\n" +
                $"Active Operations: {snapshot.Operation?.ActiveCount ?? 0} | Commands: {snapshot.Command?.ActiveCount ?? 0}\n" +
                $"Missing Resources: {snapshot.MissingResources?.UniqueCount ?? 0} ({snapshot.MissingResources?.TotalCount ?? 0} uses)\n" +
                $"Playback Mode: {snapshot.Playback?.Mode ?? "Manual"} (History: {snapshot.Playback?.HistoryItemCount ?? 0})\n" +
                $"State Status: {snapshot.StateDiagnostics?.Status.ToString() ?? "Idle"}";
        }

        if (logTextLabel is not null)
        {
            var logs = snapshot.RecentLogs;
            if (logs is null || logs.Count == 0)
            {
                logTextLabel.Text = "No recent log events.";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (var i = Math.Max(0, logs.Count - 50); i < logs.Count; i++)
                {
                    sb.AppendLine(GodotConsoleSink.FormatEvent(logs[i]));
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
            AnchorBottom = 1.0f,
            Visible = false,
        };
        AddChild(rootControl);

        var backdrop = new ColorRect
        {
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            Color = new Color(0.05f, 0.05f, 0.08f, 0.92f),
        };
        rootControl.AddChild(backdrop);

        var margin = new MarginContainer
        {
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
        };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        rootControl.AddChild(margin);

        var vbox = new VBoxContainer();
        margin.AddChild(vbox);

        var headerBox = new HBoxContainer();
        vbox.AddChild(headerBox);

        var titleLabel = new Label
        {
            Text = "Majo Runtime Diagnostics Inspector",
        };
        headerBox.AddChild(titleLabel);

        var closeButton = new Button
        {
            Text = " Close (F3/Toggle) ",
        };
        closeButton.Pressed += Close;
        headerBox.AddChild(closeButton);

        var refreshButton = new Button
        {
            Text = " Refresh ",
        };
        refreshButton.Pressed += Refresh;
        headerBox.AddChild(refreshButton);

        var copySummaryButton = new Button
        {
            Text = " Copy Summary ",
        };
        copySummaryButton.Pressed += HandleCopySummary;
        headerBox.AddChild(copySummaryButton);

        var exportButton = new Button
        {
            Text = " Export Report ",
        };
        exportButton.Pressed += HandleExportReport;
        headerBox.AddChild(exportButton);

        statusLabel = new Label
        {
            Text = string.Empty,
        };
        vbox.AddChild(statusLabel);

        summaryLabel = new Label
        {
            Text = "Loading diagnostics snapshot...",
        };
        vbox.AddChild(summaryLabel);

        var logHeader = new Label
        {
            Text = "Recent Log Events:",
        };
        vbox.AddChild(logHeader);

        logTextLabel = new RichTextLabel
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollFollowing = true,
            BbcodeEnabled = false,
        };
        vbox.AddChild(logTextLabel);
    }

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

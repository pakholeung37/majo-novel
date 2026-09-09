#nullable enable

using System;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Resources;

namespace Majo.Runtime.UI.Diagnostics;

public partial class DefaultFpsOverlay : CanvasLayer, IDisposable
{
    private readonly Node hostNode;
    private readonly DiagnosticsConfiguration configuration;
    private Control? rootControl;
    private Label? fpsLabel;
    private Label? memoryLabel;
    private readonly IResourceManager? resources;
    private readonly ResourceMemorySampler memorySampler = new();
    private readonly System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
    private double memoryElapsed = 1;
    private bool isDisposed;
    private double elapsedSinceUpdate;

    private const double UpdateIntervalSeconds = 0.1;

    public DefaultFpsOverlay(
        Node hostNode,
        DiagnosticsConfiguration? configuration = null,
        IResourceManager? resources = null)
    {
        this.resources = resources;
        this.hostNode = hostNode ?? throw new ArgumentNullException(nameof(hostNode));
        this.configuration = configuration ?? new DiagnosticsConfiguration();
        Layer = 128;
        BuildUi();
        hostNode.AddChild(this);
        UpdateFps();
    }

    public Label? FpsLabel => fpsLabel;

    public override void _Process(double delta)
    {
        if (isDisposed || !Visible || rootControl is null || !rootControl.Visible)
        {
            return;
        }


        memoryElapsed += delta;
        if (memoryElapsed >= 1)
        {
            memoryElapsed = 0;
            UpdateMemory();
        }
        elapsedSinceUpdate += delta;
        if (elapsedSinceUpdate >= UpdateIntervalSeconds)
        {
            elapsedSinceUpdate = 0;
            UpdateFps();
        }
    }

    public void UpdateFps()
    {
        if (fpsLabel is null || isDisposed)
        {
            return;
        }

        var fps = Engine.GetFramesPerSecond();
        fpsLabel.Text = $"FPS: {Mathf.RoundToInt(fps)}";
    }

    private void UpdateMemory()
    {
        if (memoryLabel is null) return;
        var ram = "N/A";
        try
        {
            process.Refresh();
            ram = FormattableString.Invariant($"{process.WorkingSet64 / 1048576.0,4:0}M");
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        catch (PlatformNotSupportedException) { }
        memoryLabel.Text = $" | RAM {ram} | " + (resources is null ? "RM N/A" : memorySampler.Sample(resources));
    }

    private void BuildUi()
    {
        var margin = new MarginContainer
        {
            Name = "MajoFpsOverlayRoot",
            AnchorLeft = 0.0f,
            AnchorTop = 0.0f,
            AnchorRight = 0.0f,
            AnchorBottom = 0.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        rootControl = margin;
        AddChild(rootControl);

        fpsLabel = new Label
        {
            Name = "FpsLabel",
            Text = "FPS: 0",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        fpsLabel.AddThemeColorOverride("font_color", new Color(0.0f, 1.0f, 0.0f));
        fpsLabel.AddThemeColorOverride("font_outline_color", new Color(0.0f, 0.0f, 0.0f, 0.85f));
        fpsLabel.AddThemeConstantOverride("outline_size", 3);
        fpsLabel.AddThemeFontSizeOverride("font_size", 16);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        rootControl.AddChild(row);
        fpsLabel.CustomMinimumSize = new Vector2(64, 0);
        row.AddChild(fpsLabel);
        memoryLabel = new Label { Name = "MemoryLabel", MouseFilter = Control.MouseFilterEnum.Ignore };
        memoryLabel.AddThemeColorOverride("font_color", Colors.White);
        memoryLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        memoryLabel.AddThemeConstantOverride("outline_size", 3);
        memoryLabel.AddThemeFontSizeOverride("font_size", 16);
        var font = new SystemFont { FontNames = new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "monospace" } };
        fpsLabel.AddThemeFontOverride("font", font);
        memoryLabel.AddThemeFontOverride("font", font);
        row.AddChild(memoryLabel);
        UpdateMemory();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !isDisposed)
        {
            isDisposed = true;
            process.Dispose();
            if (IsInsideTree())
            {
                QueueFree();
            }
        }

        base.Dispose(disposing);
    }
}

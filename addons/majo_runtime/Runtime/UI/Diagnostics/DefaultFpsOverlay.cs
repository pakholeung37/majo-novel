#nullable enable

using System;
using Godot;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.UI.Diagnostics;

public partial class DefaultFpsOverlay : CanvasLayer, IDisposable
{
    private readonly Node hostNode;
    private readonly DiagnosticsConfiguration configuration;
    private Control? rootControl;
    private Label? fpsLabel;
    private bool isDisposed;
    private double elapsedSinceUpdate;

    private const double UpdateIntervalSeconds = 0.1;

    public DefaultFpsOverlay(
        Node hostNode,
        DiagnosticsConfiguration? configuration = null)
    {
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
        rootControl.AddChild(fpsLabel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !isDisposed)
        {
            isDisposed = true;
            if (IsInsideTree())
            {
                QueueFree();
            }
        }

        base.Dispose(disposing);
    }
}

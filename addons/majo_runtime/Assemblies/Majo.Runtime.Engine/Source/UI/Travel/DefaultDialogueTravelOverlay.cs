#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Travel;

namespace Majo.Runtime.UI.Travel;

public sealed class DefaultDialogueTravelOverlay : IRuntimeService, IDisposable
{
    private readonly Node owner;
    private readonly IDialogueTravelManager travel;
    private readonly IInputManager input;
    private readonly IPlaybackModeManager playback;
    private CanvasLayer? layer;
    private Control? root;
    private VBoxContainer? entries;
    private Label? statusLabel;
    private Button? backButton;
    private Button? forwardButton;
    private IInputHandle? toggleInput;
    private IInputHandle? modalInput;
    private IPlaybackPauseHandle? playbackPause;
    private bool isOpen;
    private bool isInitialized;

    public DefaultDialogueTravelOverlay(
        Node owner,
        IDialogueTravelManager travel,
        IInputManager input,
        IPlaybackModeManager playback)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.travel = travel ?? throw new ArgumentNullException(nameof(travel));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        root = BuildView();
        layer = new CanvasLayer
        {
            Name = "MajoDialogueTravelLayer",
            Layer = 110,
        };
        owner.AddChild(layer);
        layer.AddChild(root);
        travel.TimelineChanged += HandleTimelineChanged;
        travel.TravelFailed += HandleTravelFailed;
        isInitialized = true;
        EnsureToggleInput();
        RebuildEntries();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();
        EnsureToggleInput();
        RebuildEntries();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        Close();
        travel.TimelineChanged -= HandleTimelineChanged;
        travel.TravelFailed -= HandleTravelFailed;
        DisposeToggleInput();
        if (layer is not null && GodotObject.IsInstanceValid(layer))
        {
            layer.Free();
        }
        else if (root is not null && GodotObject.IsInstanceValid(root))
        {
            root.Free();
        }

        layer = null;
        root = null;
        entries = null;
        statusLabel = null;
        backButton = null;
        forwardButton = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void Open()
    {
        if (!isInitialized || isOpen || root is null)
        {
            return;
        }

        modalInput = input.AcquireScope(
            this,
            new InputScopeOptions(
                [
                    MajoInputActions.Travel,
                    MajoInputActions.Cancel,
                    MajoInputActions.StepBack,
                    MajoInputActions.StepForward,
                ],
                isModal: true,
                name: "dialogue-travel-modal"));
        modalInput.Activated += HandleModalInput;
        playbackPause = playback.AcquirePause(PlaybackPauseReason.External);
        isOpen = true;
        root.Visible = true;
        RebuildEntries();
    }

    public void Close()
    {
        if (!isOpen)
        {
            return;
        }

        if (modalInput is not null)
        {
            modalInput.Activated -= HandleModalInput;
            modalInput.Dispose();
            modalInput = null;
        }
        playbackPause?.Dispose();
        playbackPause = null;
        isOpen = false;
        if (root is not null)
        {
            root.Visible = false;
        }
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

    private PanelContainer BuildView()
    {
        var panel = new PanelContainer
        {
            Name = "MajoDialogueTravelOverlay",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000,
        };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 48);
        margin.AddThemeConstantOverride("margin_top", 48);
        margin.AddThemeConstantOverride("margin_right", 48);
        margin.AddThemeConstantOverride("margin_bottom", 48);
        panel.AddChild(margin);

        var layout = new VBoxContainer();
        margin.AddChild(layout);
        var header = new HBoxContainer();
        layout.AddChild(header);
        header.AddChild(new Label
        {
            Text = "Dialogue Travel",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });

        backButton = new Button { Text = "Previous" };
        backButton.Pressed += () => _ = RunTravelAsync(travel.StepBackAsync);
        header.AddChild(backButton);
        forwardButton = new Button { Text = "Next" };
        forwardButton.Pressed += () => _ = RunTravelAsync(travel.StepForwardAsync);
        header.AddChild(forwardButton);
        var clearButton = new Button { Text = "Clear" };
        clearButton.Pressed += travel.Clear;
        header.AddChild(clearButton);
        var closeButton = new Button { Text = "Close" };
        closeButton.Pressed += Close;
        header.AddChild(closeButton);

        statusLabel = new Label();
        layout.AddChild(statusLabel);
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        layout.AddChild(scroll);
        entries = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(entries);
        return panel;
    }

    private void RebuildEntries()
    {
        if (entries is null)
        {
            return;
        }

        foreach (var child in entries.GetChildren())
        {
            child.Free();
        }

        var snapshot = travel.Entries;
        var current = travel.CurrentIndex;
        if (backButton is not null)
        {
            backButton.Disabled = !travel.CanStepBack;
        }
        if (forwardButton is not null)
        {
            forwardButton.Disabled = !travel.CanStepForward;
        }
        if (statusLabel is not null)
        {
            statusLabel.Text = snapshot.Count == 0
                ? "No stable dialogue checkpoints captured yet."
                : $"{snapshot.Count}/{travel.Capacity} checkpoints · current {current + 1}";
        }

        for (var index = snapshot.Count - 1; index >= 0; index--)
        {
            var entry = snapshot[index];
            var marker = index == current ? "▶ " : string.Empty;
            var speaker = string.IsNullOrWhiteSpace(entry.Speaker)
                ? string.Empty
                : $"{entry.Speaker}: ";
            var button = new Button
            {
                Text = $"{marker}{entry.Sequence} · {entry.Spot.Script.NodeName} · " +
                    $"{speaker}{entry.Text}",
                Alignment = HorizontalAlignment.Left,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = $"{entry.Spot.Script.ProjectId}:" +
                    $"{entry.Spot.Script.NodeName}#{entry.Spot.LineId}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Disabled = index == current,
            };
            var sequence = entry.Sequence;
            button.Pressed += () => _ = RunTravelAsync(
                cancellationToken => travel.TravelToAsync(sequence, cancellationToken));
            entries.AddChild(button);
        }
    }

    private async Task RunTravelAsync(
        Func<CancellationToken, Task<bool>> operation)
    {
        Close();
        try
        {
            await operation(CancellationToken.None);
        }
        catch
        {
            Open();
        }
    }

    private void HandleTimelineChanged(DialogueTravelTimelineChanged _) => RebuildEntries();

    private void HandleTravelFailed(DialogueTravelFailed failure)
    {
        if (statusLabel is not null)
        {
            statusLabel.Text = $"Travel failed: {failure.Exception.Message}";
        }
    }

    private void EnsureToggleInput()
    {
        if (!isInitialized || toggleInput is { IsDisposed: false })
        {
            return;
        }

        DisposeToggleInput();
        toggleInput = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.Travel], name: "dialogue-travel-toggle"));
        toggleInput.Activated += HandleToggleInput;
    }

    private void DisposeToggleInput()
    {
        if (toggleInput is null)
        {
            return;
        }

        toggleInput.Activated -= HandleToggleInput;
        toggleInput.Dispose();
        toggleInput = null;
    }

    private void HandleToggleInput(InputActivation _) => Toggle();

    private void HandleModalInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.StepBack)
        {
            _ = RunTravelAsync(travel.StepBackAsync);
            return;
        }
        if (activation.Action == MajoInputActions.StepForward)
        {
            _ = RunTravelAsync(travel.StepForwardAsync);
            return;
        }
        if (activation.Action == MajoInputActions.Travel ||
            activation.Action == MajoInputActions.Cancel)
        {
            Close();
        }
    }

    public void Dispose()
    {
        Close();
        DisposeToggleInput();
        if (GodotObject.IsInstanceValid(layer))
        {
            layer.QueueFree();
            layer.Dispose();
        }

        layer = null;
        root = null;
        entries = null;
        statusLabel = null;
        backButton = null;
        forwardButton = null;
    }
}

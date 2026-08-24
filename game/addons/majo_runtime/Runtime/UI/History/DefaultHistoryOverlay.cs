#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.History;

namespace Majo.Runtime.UI.History;

public sealed class DefaultHistoryOverlay : IRuntimeService
{
    private readonly Node owner;
    private readonly IHistoryManager history;
    private CanvasLayer? layer;
    private Control? root;
    private VBoxContainer? entries;
    private Label? emptyLabel;
    private bool isInitialized;

    public DefaultHistoryOverlay(Node owner, IHistoryManager history)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
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
            Name = "MajoHistoryLayer",
            Layer = 40,
        };
        owner.AddChild(layer);
        layer.AddChild(root);
        history.OpenStateChanged += HandleOpenStateChanged;
        history.EntriesChanged += HandleEntriesChanged;
        isInitialized = true;
        RebuildEntries();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root is not null)
        {
            root.Visible = false;
        }

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

        history.OpenStateChanged -= HandleOpenStateChanged;
        history.EntriesChanged -= HandleEntriesChanged;
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
        emptyLabel = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    private Control BuildView()
    {
        var panel = new PanelContainer
        {
            Name = "MajoHistoryOverlay",
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
            Text = "History",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        var close = new Button { Text = "Close" };
        close.Pressed += history.Close;
        header.AddChild(close);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        layout.AddChild(scroll);
        entries = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(entries);
        emptyLabel = new Label
        {
            Text = "No completed dialogue in this session.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        entries.AddChild(emptyLabel);
        return panel;
    }

    private void HandleOpenStateChanged(HistoryOpenStateChanged change)
    {
        if (root is not null)
        {
            root.Visible = change.IsOpen;
        }
    }

    private void HandleEntriesChanged(HistoryEntriesChanged _) => RebuildEntries();

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

        if (history.Entries.Count == 0)
        {
            emptyLabel = new Label
            {
                Text = "No completed dialogue in this session.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            entries.AddChild(emptyLabel);
            return;
        }

        emptyLabel = null;
        foreach (var entry in history.Entries)
        {
            var speaker = string.IsNullOrWhiteSpace(entry.Speaker)
                ? string.Empty
                : $"{entry.Speaker}: ";
            var button = new Button
            {
                Text = $"{speaker}{entry.Text}",
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                Alignment = HorizontalAlignment.Left,
                Disabled = entry.VoiceId is null,
                TooltipText = entry.VoiceId is null
                    ? "No replayable voice"
                    : $"Replay voice: {entry.VoiceId}",
            };
            var sequence = entry.Sequence;
            button.Pressed += () => _ = ReplayVoiceAsync(sequence);
            entries.AddChild(button);
        }
    }

    private async Task ReplayVoiceAsync(long sequence)
    {
        try
        {
            await history.ReplayVoiceAsync(sequence);
        }
        catch (Exception)
        {
            // HistoryManager owns diagnostic logging for replay failures.
        }
    }
}

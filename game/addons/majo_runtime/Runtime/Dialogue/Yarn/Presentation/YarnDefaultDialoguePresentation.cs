#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed class YarnDefaultDialoguePresentation : IDialoguePresentation
{
    private readonly IYarnDialogueBackend backend;
    private readonly IInputManager input;
    private readonly IFrameClock frameClock;
    private readonly DialoguePresentationConfiguration configuration;
    private readonly IReadOnlyList<IDialogueMarkupHandler> markupHandlers;
    private Control? root;
    private LinePresenter? linePresenter;
    private OptionsPresenter? optionsPresenter;
    private MajoActionMarkupBridge? markupBridge;
    private MajoPresentationInputPresenter? inputPresenter;
    private bool isInitialized;

    public YarnDefaultDialoguePresentation(
        IYarnDialogueBackend backend,
        IInputManager input,
        IFrameClock frameClock,
        DialoguePresentationConfiguration configuration,
        IReadOnlyList<IDialogueMarkupHandler> markupHandlers)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        this.markupHandlers = markupHandlers ??
            throw new ArgumentNullException(nameof(markupHandlers));
    }

    public bool IsLineActive => markupBridge?.IsLineActive == true;

    public bool IsLineDisplayComplete => markupBridge?.IsDisplayComplete == true;

    public bool IsPresentingOptions => inputPresenter?.IsPresentingOptions == true;

    public event Action? LineDisplayCompleted;

    public event Action? LineDismissed;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        // Mark the service before creating native objects so compensation
        // shutdown can release a partially constructed Presenter scene.
        isInitialized = true;
        if (!configuration.Enabled)
        {
            return Task.CompletedTask;
        }

        var scene = configuration.PresenterScene ??
            ResourceLoader.Load<PackedScene>(DialoguePresentationConfiguration.DefaultScenePath);
        if (scene is null)
        {
            throw new DialogueException(
                $"Default dialogue Presenter scene '{DialoguePresentationConfiguration.DefaultScenePath}' " +
                "could not be loaded.");
        }

        var instance = scene.Instantiate();
        if (instance is not Control control)
        {
            instance.Free();
            throw new DialogueException(
                "Dialogue Presenter PackedScene root must be a Godot Control.");
        }

        root = control;
        root.Name = "MajoDefaultDialoguePresentation";
        linePresenter = FindRequired<LinePresenter>(root, "LinePresenter");
        optionsPresenter = FindRequired<OptionsPresenter>(root, "OptionsPresenter");
        var optionParent = FindRequired<Node>(root, "OptionContainer");

        ConfigureView(root, linePresenter);
        linePresenter.dialogueRunner = backend.Runner;
        markupBridge = new MajoActionMarkupBridge(backend.Runner, markupHandlers)
        {
            Name = "MajoMarkupBridge",
        };
        markupBridge.DisplayCompleted += HandleLineDisplayCompleted;
        markupBridge.LineDismissed += HandleLineDismissed;
        linePresenter.AddChild(markupBridge);
        inputPresenter = new MajoPresentationInputPresenter(input, markupBridge, optionParent)
        {
            Name = "MajoInputPresenter",
        };
        root.AddChild(inputPresenter);

        backend.Runner.AddChild(root);
        linePresenter.ActionMarkupHandlers.Add(markupBridge);
        backend.RegisterNativePresenter(linePresenter);
        backend.RegisterNativePresenter(optionsPresenter);
        backend.RegisterNativePresenter(inputPresenter);
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inputPresenter?.ResetInput();
        markupBridge?.ResetState();
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return;
        }

        inputPresenter?.ResetInput();
        if (markupBridge is not null)
        {
            markupBridge.DisplayCompleted -= HandleLineDisplayCompleted;
            markupBridge.LineDismissed -= HandleLineDismissed;
        }

        UnregisterPresenter(inputPresenter);
        UnregisterPresenter(optionsPresenter);
        UnregisterPresenter(linePresenter);
        if (linePresenter is not null && markupBridge is not null &&
            GodotObject.IsInstanceValid(linePresenter) &&
            GodotObject.IsInstanceValid(markupBridge))
        {
            linePresenter.ActionMarkupHandlers.Remove(markupBridge);
        }

        if (root is not null && GodotObject.IsInstanceValid(root))
        {
            // DialogueRunner.Stop can settle before native presenter cleanup has
            // crossed its final NextFrame continuations. Keep the nodes alive until
            // Yarn's line/options tasks have retired, then release the owned scene.
            if (root.IsInsideTree())
            {
                await frameClock.NextFrameAsync(CancellationToken.None);
                await frameClock.NextFrameAsync(CancellationToken.None);
            }

            // The presentation root is created and exclusively owned by this
            // service. Free it synchronously so no native presenter or markup
            // handler survives until a later SceneTree frame.
            root.Free();
        }

        root = null;
        linePresenter = null;
        optionsPresenter = null;
        markupBridge = null;
        inputPresenter = null;
        isInitialized = false;
    }

    private void HandleLineDisplayCompleted() => LineDisplayCompleted?.Invoke();

    private void HandleLineDismissed() => LineDismissed?.Invoke();

    private void UnregisterPresenter(Node? presenter)
    {
        if (presenter is not null && GodotObject.IsInstanceValid(presenter))
        {
            backend.UnregisterNativePresenter(presenter);
        }
    }

    private void ConfigureView(Control viewRoot, LinePresenter presenter)
    {
        if (configuration.Theme is not null)
        {
            viewRoot.Theme = configuration.Theme;
        }

        presenter.typewriterStyle = LinePresenter.TypewriterType.ByLetter;
        presenter.lettersPerSecond = configuration.LettersPerSecond;
        presenter.useFadeEffect = configuration.UseFadeEffect;
        presenter.fadeUpDuration = (float)configuration.FadeUpDurationSeconds;
        presenter.fadeDownDuration = (float)configuration.FadeDownDurationSeconds;

        foreach (var label in Descendants(viewRoot).OfType<RichTextLabel>())
        {
            label.AddThemeFontSizeOverride("normal_font_size", configuration.FontSize);
            label.AddThemeFontSizeOverride("bold_font_size", configuration.FontSize);
            label.AddThemeFontSizeOverride("italics_font_size", configuration.FontSize);
            label.AddThemeFontSizeOverride("bold_italics_font_size", configuration.FontSize);
            label.AddThemeFontSizeOverride("mono_font_size", configuration.FontSize);
            if (configuration.Font is null)
            {
                continue;
            }

            label.AddThemeFontOverride("normal_font", configuration.Font);
            label.AddThemeFontOverride("bold_font", configuration.Font);
            label.AddThemeFontOverride("italics_font", configuration.Font);
            label.AddThemeFontOverride("bold_italics_font", configuration.Font);
            label.AddThemeFontOverride("mono_font", configuration.Font);
        }
    }

    private static TNode FindRequired<TNode>(Node owner, string name)
        where TNode : Node
    {
        var found = owner.FindChild(name, recursive: true, owned: false);
        if (found is not TNode typed)
        {
            throw new DialogueException(
                $"Dialogue Presenter PackedScene requires a '{name}' node of type " +
                $"{typeof(TNode).Name}.");
        }

        return typed;
    }

    private static IEnumerable<Node> Descendants(Node owner)
    {
        foreach (var child in owner.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}

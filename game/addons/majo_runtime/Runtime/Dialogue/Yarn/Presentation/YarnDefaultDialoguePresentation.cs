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
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed class YarnDefaultDialoguePresentation : IDialoguePresentation, IDialoguePresentationSettings
{
    private readonly IYarnDialogueBackend backend;
    private readonly IInputManager input;
    private readonly IFrameClock frameClock;
    private readonly DialoguePresentationConfiguration configuration;
    private readonly IReadOnlyList<IDialogueMarkupHandler> markupHandlers;
    private readonly ILocaleManager? localeManager;
    private readonly IFontCatalog? fontCatalog;
    private CanvasLayer? presentationLayer;
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
        IReadOnlyList<IDialogueMarkupHandler> markupHandlers,
        ILocaleManager? localeManager = null,
        IFontCatalog? fontCatalog = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        this.markupHandlers = markupHandlers ??
            throw new ArgumentNullException(nameof(markupHandlers));
        this.localeManager = localeManager;
        this.fontCatalog = fontCatalog;
    }

    public bool IsLineActive => markupBridge?.IsLineActive == true;

    public bool IsLineDisplayComplete => markupBridge?.IsDisplayComplete == true;

    public bool IsPresentingOptions => inputPresenter?.IsPresentingOptions == true;

    public int LettersPerSecond
    {
        get => configuration.LettersPerSecond;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            configuration.LettersPerSecond = value;
            if (linePresenter is not null) linePresenter.lettersPerSecond = value;
        }
    }

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
        presentationLayer = new CanvasLayer
        {
            Name = "MajoDialoguePresentationLayer",
            Layer = 20,
        };
        backend.Runner.AddChild(presentationLayer);
        presentationLayer.AddChild(root);
        linePresenter = FindRequired<LinePresenter>(root, "LinePresenter");
        optionsPresenter = FindRequired<OptionsPresenter>(root, "OptionsPresenter");
        var optionParent = FindRequired<Node>(root, "OptionContainer");

        ConfigureView(root, linePresenter);
        linePresenter.dialogueRunner = backend.Runner;
        markupBridge = new MajoActionMarkupBridge(
            backend.RequestHurryUp,
            backend.RequestNextLine,
            markupHandlers)
        {
            Name = "MajoMarkupBridge",
        };
        markupBridge.DisplayCompleted += HandleLineDisplayCompleted;
        markupBridge.LineDismissed += HandleLineDismissed;
        linePresenter.AddChild(markupBridge);
        backend.SetPresentationQuiescenceWaiter(markupBridge.WaitUntilInactiveAsync);
        inputPresenter = new MajoPresentationInputPresenter(input, markupBridge, optionParent)
        {
            Name = "MajoInputPresenter",
        };
        root.AddChild(inputPresenter);

        if (fontCatalog is not null)
        {
            fontCatalog.TypographyChanged += HandleTypographyChanged;
        }
        if (localeManager is not null)
        {
            localeManager.DialogueLocaleChanged += HandleLocaleChanged;
        }

        linePresenter.ActionMarkupHandlers.Add(new MajoBbCodeMarkupHandler());
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

        if (fontCatalog is not null)
        {
            fontCatalog.TypographyChanged -= HandleTypographyChanged;
        }
        if (localeManager is not null)
        {
            localeManager.DialogueLocaleChanged -= HandleLocaleChanged;
        }

        inputPresenter?.ResetInput();
        backend.SetPresentationQuiescenceWaiter(null);
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
            if (presentationLayer is not null &&
                GodotObject.IsInstanceValid(presentationLayer))
            {
                presentationLayer.Free();
            }
            else
            {
                root.Free();
            }
        }

        presentationLayer = null;
        root = null;
        linePresenter = null;
        optionsPresenter = null;
        markupBridge = null;
        inputPresenter = null;
        isInitialized = false;
    }

    public void ReRenderLine(DialogueLine line)
    {
        if (linePresenter is null || !IsLineActive)
        {
            return;
        }

        if (linePresenter.characterNameText is not null)
        {
            linePresenter.characterNameText.Text = line.Speaker ?? string.Empty;
            if (linePresenter.characterNameContainer is not null)
            {
                linePresenter.characterNameContainer.Visible = !string.IsNullOrWhiteSpace(line.Speaker);
            }
        }

        if (linePresenter.lineText is not null)
        {
            linePresenter.lineText.Text = line.Text.Text;
            linePresenter.lineText.VisibleRatio = 1.0f;
        }
    }

    public void ReRenderOptions(IReadOnlyList<DialogueOption> options)
    {
        if (root is null || !IsPresentingOptions)
        {
            return;
        }

        var optionContainer = root.FindChild("OptionContainer", recursive: true, owned: false);
        if (optionContainer is null)
        {
            return;
        }

        var optionButtons = optionContainer.GetChildren().OfType<Button>().ToList();
        for (var i = 0; i < options.Count && i < optionButtons.Count; i++)
        {
            optionButtons[i].Text = options[i].Line.Text.Text;
        }
    }

    private void HandleTypographyChanged() => ApplyTypography();

    private void HandleLocaleChanged(LocaleChangedEvent _) => ApplyTypography();

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

        ApplyTypography();
    }

    private void ApplyTypography()
    {
        if (root is null)
        {
            return;
        }

        var scale = fontCatalog?.FontScale ?? 1.0;
        var scaledFontSize = Math.Max(8, (int)Math.Round(configuration.FontSize * scale));
        var lineSpacing = fontCatalog?.LineSpacing ?? 0;
        var currentLocale = localeManager?.DialogueLocale ?? "en";
        var fallbackChain = localeManager?.GetFallbackChain(currentLocale) ?? [currentLocale];
        var font = fontCatalog?.ResolveFont(currentLocale, fallbackChain) ?? configuration.Font;

        foreach (var label in Descendants(root).OfType<RichTextLabel>())
        {
            label.AddThemeFontSizeOverride("normal_font_size", scaledFontSize);
            label.AddThemeFontSizeOverride("bold_font_size", scaledFontSize);
            label.AddThemeFontSizeOverride("italics_font_size", scaledFontSize);
            label.AddThemeFontSizeOverride("bold_italics_font_size", scaledFontSize);
            label.AddThemeFontSizeOverride("mono_font_size", scaledFontSize);

            if (lineSpacing > 0)
            {
                label.AddThemeConstantOverride("line_separation", lineSpacing);
            }

            if (font is not null)
            {
                label.AddThemeFontOverride("normal_font", font);
                label.AddThemeFontOverride("bold_font", font);
                label.AddThemeFontOverride("italics_font", font);
                label.AddThemeFontOverride("bold_italics_font", font);
                label.AddThemeFontOverride("mono_font", font);
            }
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

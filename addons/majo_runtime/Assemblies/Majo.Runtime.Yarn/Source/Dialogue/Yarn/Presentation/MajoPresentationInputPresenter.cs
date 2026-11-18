#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using Majo.Runtime.Input;
using YarnSpinnerGodot;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed partial class MajoPresentationInputPresenter : Node, DialoguePresenterBase
{
    private readonly IInputManager input;
    private readonly MajoActionMarkupBridge lineBridge;
    private readonly Node optionParent;
    private IInputHandle? lineScope;
    private IInputHandle? optionScope;
    private int selectedOptionIndex;

    public MajoPresentationInputPresenter(
        IInputManager input,
        MajoActionMarkupBridge lineBridge,
        Node optionParent)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.lineBridge = lineBridge ?? throw new ArgumentNullException(nameof(lineBridge));
        this.optionParent = optionParent ?? throw new ArgumentNullException(nameof(optionParent));
    }

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public bool IsPresentingOptions { get; private set; }

    public YarnTask OnDialogueStartedAsync()
    {
        ReleaseInput();
        lineScope = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.Advance], name: "default-vn-line"));
        lineScope.Activated += HandleLineInput;
        return YarnTask.CompletedTask;
    }

    public YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token) =>
        YarnTask.CompletedTask;

    public async YarnTask<YarnDialogueOption?> RunOptionsAsync(
        YarnDialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        IsPresentingOptions = true;
        selectedOptionIndex = 0;
        optionScope = input.AcquireScope(
            this,
            new InputScopeOptions(
                [
                    MajoInputActions.OptionUp,
                    MajoInputActions.OptionDown,
                    MajoInputActions.Confirm,
                    MajoInputActions.Advance,
                    MajoInputActions.Cancel,
                ],
                isModal: true,
                name: "default-vn-options"));
        optionScope.Activated += HandleOptionInput;

        try
        {
            await YarnTask.WaitUntilCanceled(cancellationToken).SuppressCancellationThrow();
        }
        finally
        {
            ReleaseOptionScope();
            IsPresentingOptions = false;
        }

        return await DialogueRunner.NoOptionSelected;
    }

    public YarnTask OnDialogueCompleteAsync()
    {
        ReleaseInput();
        lineBridge.ResetState();
        return YarnTask.CompletedTask;
    }

    public void ResetInput()
    {
        ReleaseInput();
        lineBridge.ResetState();
    }

    private void HandleLineInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.Advance)
        {
            lineBridge.Advance();
        }
    }

    private void HandleOptionInput(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.OptionUp)
        {
            MoveSelection(-1);
        }
        else if (activation.Action == MajoInputActions.OptionDown)
        {
            MoveSelection(1);
        }
        else if (activation.Action == MajoInputActions.Confirm ||
            (activation.Action == MajoInputActions.Advance &&
                activation.Device != InputDeviceKind.Mouse))
        {
            SelectCurrentOption();
        }
    }

    private void MoveSelection(int offset)
    {
        var options = GetAvailableOptions();
        if (options.Length == 0)
        {
            return;
        }

        selectedOptionIndex = (selectedOptionIndex + offset + options.Length) % options.Length;
        options[selectedOptionIndex].FocusButton();
    }

    private void SelectCurrentOption()
    {
        var options = GetAvailableOptions();
        if (options.Length == 0)
        {
            return;
        }

        selectedOptionIndex = Math.Clamp(selectedOptionIndex, 0, options.Length - 1);
        options[selectedOptionIndex].InvokeOptionSelected();
    }

    private OptionItem[] GetAvailableOptions() => optionParent
        .GetChildren()
        .OfType<OptionItem>()
        .Where(option =>
            option.Visible &&
            option.Option is { IsAvailable: true })
        .ToArray();

    private void ReleaseInput()
    {
        ReleaseOptionScope();
        if (lineScope is not null)
        {
            lineScope.Activated -= HandleLineInput;
            lineScope.Dispose();
            lineScope = null;
        }

        IsPresentingOptions = false;
    }

    private void ReleaseOptionScope()
    {
        if (optionScope is null)
        {
            return;
        }

        optionScope.Activated -= HandleOptionInput;
        optionScope.Dispose();
        optionScope = null;
    }
}

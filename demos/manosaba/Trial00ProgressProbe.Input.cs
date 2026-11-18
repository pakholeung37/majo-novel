#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Input;

namespace Majo.Demos.Manosaba;

public partial class Trial00ProgressProbe
{
    private readonly List<InputSample> inputSamples = new();
    private IInputManager? input;
    private IDialoguePresentation? presentation;
    private RichTextLabel? lineText;
    private InputSample? pendingInput;
    private bool measureInput;
    private bool contentInSubmission;
    private string previousText = "";

    private void AttachInputMeasurement(MajoRuntimeBootstrap runtime)
    {
        measureInput = Array.IndexOf(OS.GetCmdlineUserArgs(), "--trial00-progress-input") >= 0;
        if (!measureInput) return;
        input = runtime.Host!.Services.Get<IInputManager>();
        presentation = runtime.Host.Services.Get<IDialoguePresentation>();
        lineText = runtime.FindChild("LineText", recursive: true, owned: false) as RichTextLabel
            ?? throw new InvalidOperationException("Trial00 input benchmark requires LineText.");
        input.Dispatched += OnInputDispatched;
        presentation.LineDisplayCompleted += OnInputDisplayCompleted;
        presentation.LineDismissed += OnInputDismissed;
        RenderingServer.FramePreDraw += OnInputPreDraw;
        RenderingServer.FramePostDraw += OnInputPostDraw;
    }

    private void InjectAdvance(string kind)
    {
        // Script waits may legitimately last several seconds. Do not inject a
        // second advance while the acknowledged request is still running.
        if (pendingInput is not null) return;
        pendingInput = new InputSample
        {
            Kind = kind,
            Line = lineNumber,
            InjectedMs = clock.Elapsed.TotalMilliseconds,
            Focused = DisplayServer.WindowIsFocused(),
        };
        inputSamples.Add(pendingInput);
        previousText = lineText!.Text;
        contentInSubmission = false;
        // ParseInputEvent exercises InputMap, GUI consumption, _UnhandledInput,
        // scopes and the normal presentation bridge. It excludes OS/device latency.
        using var press = new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true };
        using var release = new InputEventKey { PhysicalKeycode = Key.Space, Pressed = false };
        Godot.Input.ParseInputEvent(press);
        Godot.Input.ParseInputEvent(release);
    }

    private void OnInputDispatched(InputActivation activation)
    {
        if (activation.Action == MajoInputActions.Advance && pendingInput is { DispatchedMs: null } sample)
            sample.DispatchedMs = clock.Elapsed.TotalMilliseconds;
    }

    private void OnInputDisplayCompleted()
    {
        if (pendingInput is { Kind: "hurry", ResponseMs: null } sample)
            sample.ResponseMs = clock.Elapsed.TotalMilliseconds;
    }

    private void OnInputDismissed()
    {
        if (pendingInput is { Kind: "advance", ResponseMs: null } sample)
            sample.ResponseMs = clock.Elapsed.TotalMilliseconds;
    }

    private void ObserveInputLine()
    {
        if (pendingInput is { Kind: "advance", NextLineMs: null } sample && lineNumber > sample.Line)
            sample.NextLineMs = clock.Elapsed.TotalMilliseconds;
    }

    private void OnInputPreDraw()
    {
        var text = lineText!;
        contentInSubmission = pendingInput is { DispatchedMs: not null, ResponseMs: not null } sample &&
            HasVisibleLineText() &&
            (sample.Kind == "hurry"
                ? presentation!.IsLineDisplayComplete &&
                    (text.VisibleCharacters < 0 || text.VisibleCharacters >= text.GetTotalCharacterCount())
                : sample.NextLineMs is not null && text.Text != previousText);
    }

    private bool HasVisibleLineText()
    {
        if (!lineText!.IsVisibleInTree() || lineText.VisibleCharacters == 0 || lineText.SelfModulate.A <= 0) return false;
        for (CanvasItem? item = lineText; item is not null; item = item.TopLevel ? null : item.GetParent() as CanvasItem)
            if (item.Modulate.A <= 0) return false;
        return true;
    }

    private void OnInputPostDraw()
    {
        if (!contentInSubmission || pendingInput is not { } sample) return;
        sample.ContentPostDrawMs = clock.Elapsed.TotalMilliseconds;
        sample.Focused &= DisplayServer.WindowIsFocused();
        pendingInput = null;
        contentInSubmission = false;
    }

    private void DetachInputMeasurement()
    {
        if (input is null) return;
        if (input is not null) input.Dispatched -= OnInputDispatched;
        if (presentation is not null)
        {
            presentation.LineDisplayCompleted -= OnInputDisplayCompleted;
            presentation.LineDismissed -= OnInputDismissed;
        }
        RenderingServer.FramePreDraw -= OnInputPreDraw;
        RenderingServer.FramePostDraw -= OnInputPostDraw;
        input = null;
        presentation = null;
    }

    private sealed class InputSample
    {
        public string Kind { get; init; } = "";
        public int Line { get; init; }
        public double InjectedMs { get; init; }
        public double? DispatchedMs { get; set; }
        public double? ResponseMs { get; set; }
        public double? NextLineMs { get; set; }
        public double? ContentPostDrawMs { get; set; }
        public bool Focused { get; set; }
        public bool TimedOut { get; set; }
        public bool TerminalOptions { get; set; }
    }
}

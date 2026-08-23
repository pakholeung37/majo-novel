#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Input.Godot;

public sealed class GodotInputBackend : IInputBackend
{
    private readonly Node owner;
    private readonly InputConfiguration configuration;
    private readonly GodotInputReceiver receiver;
    private bool isInitialized;

    public GodotInputBackend(Node owner, InputConfiguration configuration)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        receiver = new GodotInputReceiver { Name = "MajoInput" };
        receiver.Activated += activation => Activated?.Invoke(activation);
    }

    public event Action<InputActivation>? Activated;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        if (configuration.InstallDefaultBindings)
        {
            InstallMissingDefaultActions();
        }

        owner.AddChild(receiver);
        receiver.SetProcessInput(true);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        receiver.SetProcessInput(true);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized && GodotObject.IsInstanceValid(receiver))
        {
            receiver.SetProcessInput(false);
            receiver.QueueFree();
        }

        isInitialized = false;
        return Task.CompletedTask;
    }

    private static void InstallMissingDefaultActions()
    {
        foreach (var definition in GodotInputDefaults.Definitions)
        {
            var godotAction = new StringName(MajoInputActions.ToGodotAction(definition.Action));
            if (InputMap.HasAction(godotAction))
            {
                continue;
            }

            InputMap.AddAction(godotAction);
            foreach (var inputEvent in definition.Events)
            {
                InputMap.ActionAddEvent(godotAction, inputEvent);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException(
                "Godot Input Backend is not initialized.");
        }
    }
}

internal partial class GodotInputReceiver : Node
{
    public event Action<InputActivation>? Activated;

    public override void _Input(InputEvent inputEvent)
    {
        foreach (var definition in GodotInputDefaults.Definitions)
        {
            var godotAction = new StringName(MajoInputActions.ToGodotAction(definition.Action));
            if (!inputEvent.IsActionPressed(godotAction, allowEcho: false))
            {
                continue;
            }

            Activated?.Invoke(new InputActivation(
                definition.Action,
                Engine.GetProcessFrames(),
                ResolveDevice(inputEvent)));
            return;
        }
    }

    private static InputDeviceKind ResolveDevice(InputEvent inputEvent) => inputEvent switch
    {
        InputEventKey => InputDeviceKind.Keyboard,
        InputEventMouse => InputDeviceKind.Mouse,
        InputEventJoypadButton or InputEventJoypadMotion => InputDeviceKind.Gamepad,
        _ => InputDeviceKind.Unknown,
    };
}

internal sealed record GodotInputActionDefinition(
    InputActionId Action,
    IReadOnlyList<InputEvent> Events);

internal static class GodotInputDefaults
{
    public static IReadOnlyList<GodotInputActionDefinition> Definitions { get; } =
    [
        new(MajoInputActions.Advance,
        [
            KeyEvent(Key.Space),
            MouseEvent(MouseButton.Left),
            JoyEvent(JoyButton.A),
        ]),
        new(MajoInputActions.Cancel,
        [
            KeyEvent(Key.Escape),
            MouseEvent(MouseButton.Right),
            JoyEvent(JoyButton.B),
        ]),
        new(MajoInputActions.Auto,
        [
            KeyEvent(Key.A),
            JoyEvent(JoyButton.Y),
        ]),
        new(MajoInputActions.History,
        [
            KeyEvent(Key.H),
            JoyEvent(JoyButton.Back),
        ]),
        new(MajoInputActions.Menu,
        [
            KeyEvent(Key.M),
            JoyEvent(JoyButton.Start),
        ]),
        new(MajoInputActions.OptionUp,
        [
            KeyEvent(Key.Up),
            MouseEvent(MouseButton.WheelUp),
            JoyEvent(JoyButton.DpadUp),
        ]),
        new(MajoInputActions.OptionDown,
        [
            KeyEvent(Key.Down),
            MouseEvent(MouseButton.WheelDown),
            JoyEvent(JoyButton.DpadDown),
        ]),
        new(MajoInputActions.Confirm,
        [
            KeyEvent(Key.Enter),
            MouseEvent(MouseButton.Middle),
            JoyEvent(JoyButton.X),
        ]),
    ];

    private static InputEventKey KeyEvent(Key key) => new()
    {
        PhysicalKeycode = key,
    };

    private static InputEventMouseButton MouseEvent(MouseButton button) => new()
    {
        ButtonIndex = button,
    };

    private static InputEventJoypadButton JoyEvent(JoyButton button) => new()
    {
        ButtonIndex = button,
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Input;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
	private async Task ValidateInputRuntimeAsync()
	{
		var existingMenuAction = new StringName(
			MajoInputActions.ToGodotAction(MajoInputActions.Menu));
		if (InputMap.HasAction(existingMenuAction))
		{
			InputMap.EraseAction(existingMenuAction);
		}

		InputMap.AddAction(existingMenuAction);
		InputMap.ActionAddEvent(existingMenuAction, new InputEventKey
		{
			PhysicalKeycode = Key.F12,
		});

		var bootstrap = new MajoRuntimeBootstrap
		{
			Name = "InputRuntime",
			Configuration = MajoProjectConfiguration.CreateDefault(),
		};
		AddChild(bootstrap);
		await bootstrap.InitializationCompletion;

		var manager = bootstrap.Host!.Services.Get<IInputManager>();
		var receiver = bootstrap.GetNode("MajoInput");
		Assert(InputMap.ActionGetEvents(existingMenuAction).Count == 1 &&
				InputMap.ActionGetEvents(existingMenuAction)[0] is InputEventKey menuKey &&
				menuKey.PhysicalKeycode == Key.F12,
			"M7 default InputMap installation overwrote an existing host binding.");
		foreach (var action in MajoInputActions.All)
		{
			Assert(InputMap.HasAction(new StringName(MajoInputActions.ToGodotAction(action))),
				$"M7 did not install missing InputMap action '{action}'.");
		}

		var owner = new Node { Name = "InputOwner" };
		AddChild(owner);
		var activations = new List<InputActivation>();
		var scope = manager.AcquireScope(
			owner,
			new InputScopeOptions(
				MajoInputActions.All,
				name: "headless-input"));
		scope.Activated += activations.Add;

		var hudButton = new Button();
		Assert(hudButton.MouseFilter == Control.MouseFilterEnum.Stop,
			"Godot HUD buttons no longer consume pointer input by default.");
		hudButton.Free();
		var handledClickCountBefore = consumedLeftClickCount;
		consumeNextLeftClick = true;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Godot.Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = true,
		});
		await WaitForConditionAsync(
			() => consumedLeftClickCount == handledClickCountBefore + 1,
			1);
		Assert(activations.Count == 0,
			"M7 allowed a GUI-handled click to bubble into Runtime input.");

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Godot.Input.ParseInputEvent(new InputEventKey
		{
			PhysicalKeycode = Key.Space,
			Pressed = true,
		});
		Godot.Input.ParseInputEvent(new InputEventKey
		{
			PhysicalKeycode = Key.Space,
			Pressed = false,
		});
		Godot.Input.ParseInputEvent(new InputEventKey
		{
			PhysicalKeycode = Key.Space,
			Pressed = true,
		});
		await WaitForConditionAsync(() => activations.Count == 1, 1);

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Godot.Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = true,
		});
		await WaitForConditionAsync(() => activations.Count == 2, 1);

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Godot.Input.ParseInputEvent(new InputEventJoypadButton
		{
			ButtonIndex = JoyButton.A,
			Pressed = true,
		});
		await WaitForConditionAsync(() => activations.Count == 3, 1);

		Assert(activations.Select(activation => activation.Action).All(
				action => action == MajoInputActions.Advance),
			"M7 keyboard, mouse and gamepad did not resolve to the same logical action.");
		Assert(activations.Select(activation => activation.Device).SequenceEqual(new[]
			{
				InputDeviceKind.Keyboard,
				InputDeviceKind.Mouse,
				InputDeviceKind.Gamepad,
			}),
			"M7 headless input injection reported incorrect device kinds.");

		var expectedSequence = new[]
		{
			MajoInputActions.Advance,
			MajoInputActions.Cancel,
			MajoInputActions.OptionUp,
			MajoInputActions.OptionDown,
			MajoInputActions.Confirm,
		};
		activations.Clear();
		await InjectSequenceAsync(
		[
			new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true },
			new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true },
			new InputEventKey { PhysicalKeycode = Key.Up, Pressed = true },
			new InputEventKey { PhysicalKeycode = Key.Down, Pressed = true },
			new InputEventKey { PhysicalKeycode = Key.Enter, Pressed = true },
		]);
		await InjectSequenceAsync(
		[
			new InputEventMouseButton
			{
				ButtonIndex = MouseButton.Left,
				Pressed = true,
			},
			new InputEventMouseButton
			{
				ButtonIndex = MouseButton.Right,
				Pressed = true,
			},
			new InputEventMouseButton
			{
				ButtonIndex = MouseButton.WheelUp,
				Pressed = true,
			},
			new InputEventMouseButton
			{
				ButtonIndex = MouseButton.WheelDown,
				Pressed = true,
			},
			new InputEventMouseButton
			{
				ButtonIndex = MouseButton.Middle,
				Pressed = true,
			},
		]);
		await InjectSequenceAsync(
		[
			new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true },
			new InputEventJoypadButton { ButtonIndex = JoyButton.B, Pressed = true },
			new InputEventJoypadButton { ButtonIndex = JoyButton.DpadUp, Pressed = true },
			new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true },
			new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = true },
		]);
		for (var deviceIndex = 0; deviceIndex < 3; deviceIndex++)
		{
			var sequence = activations
				.Skip(deviceIndex * expectedSequence.Length)
				.Take(expectedSequence.Length)
				.Select(activation => activation.Action);
			Assert(sequence.SequenceEqual(expectedSequence),
				"M7 keyboard, mouse and gamepad produced different logical sequences.");
		}

		var advanceAction = new StringName(
			MajoInputActions.ToGodotAction(MajoInputActions.Advance));
		var overlappingEnterEvent = new InputEventKey
		{
			PhysicalKeycode = Key.Enter,
		};
		InputMap.ActionAddEvent(advanceAction, overlappingEnterEvent);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Godot.Input.ParseInputEvent(new InputEventKey
		{
			PhysicalKeycode = Key.Enter,
			Pressed = true,
		});
		await WaitForConditionAsync(() => activations.Count == 16, 1);
		Assert(activations[^1].Action == MajoInputActions.Advance,
			"M7 emitted both advance and confirm for one overlapping physical binding.");

		async Task InjectSequenceAsync(IEnumerable<InputEvent> inputEvents)
		{
			foreach (var inputEvent in inputEvents)
			{
				var expectedCount = activations.Count + 1;
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Godot.Input.ParseInputEvent(inputEvent);
				await WaitForConditionAsync(() => activations.Count == expectedCount, 1);
			}
		}

		owner.QueueFree();
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Assert(scope.IsDisposed && manager.ActiveHandleCount == 2,
			"M7 did not release an input Scope when its Node owner exited the tree.");

		var resetHandle = manager.AcquireMuter(new object());
		await bootstrap.ResetRuntimeAsync();
		Assert(resetHandle.IsDisposed && manager.ActiveHandleCount == 2,
			"M7 Reset left an input Muter active or failed to rebind M10 controls.");

		await bootstrap.RequestShutdownAsync();
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Assert(!IsInstanceValid(receiver),
			"M7 Runtime shutdown left the Godot input receiver behind.");
		Assert(!IsInstanceValid(bootstrap), "M7 Input Runtime node leaked after shutdown.");
		Assert(!IsInstanceValid(owner), "M7 input owner node leaked after cleanup.");
		InputMap.ActionEraseEvent(advanceAction, overlappingEnterEvent);
		InputMap.EraseAction(existingMenuAction);
	}
}

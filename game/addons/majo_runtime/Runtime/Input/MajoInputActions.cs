#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Input;

public static class MajoInputActions
{
    public static readonly InputActionId Advance = new("advance");

    public static readonly InputActionId Cancel = new("cancel");

    public static readonly InputActionId Auto = new("auto");

    public static readonly InputActionId History = new("history");

    public static readonly InputActionId Menu = new("menu");

    public static readonly InputActionId QuickSave = new("quickSave");

    public static readonly InputActionId OptionUp = new("optionUp");

    public static readonly InputActionId OptionDown = new("optionDown");

    public static readonly InputActionId Confirm = new("confirm");

    public static readonly InputActionId Debug = new("debug");

    public static IReadOnlyList<InputActionId> All { get; } = new List<InputActionId>
    {
        Advance,
        Cancel,
        Auto,
        History,
        Menu,
        QuickSave,
        OptionUp,
        OptionDown,
        Confirm,
        Debug,
    }.AsReadOnly();

    public static string ToGodotAction(InputActionId action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action == OptionUp
            ? "majo_option_up"
            : action == OptionDown
                ? "majo_option_down"
                : $"majo_{action.Value}";
    }

    public static InputActionId FromGodotAction(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (!action.StartsWith("majo_", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Majo Godot actions must start with 'majo_'.",
                nameof(action));
        }

        return action switch
        {
            "majo_option_up" => OptionUp,
            "majo_option_down" => OptionDown,
            _ => new InputActionId(action["majo_".Length..]),
        };
    }
}

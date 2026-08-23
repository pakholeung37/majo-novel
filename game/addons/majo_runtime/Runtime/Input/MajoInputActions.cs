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

    public static readonly InputActionId OptionUp = new("optionUp");

    public static readonly InputActionId OptionDown = new("optionDown");

    public static readonly InputActionId Confirm = new("confirm");

    public static IReadOnlyList<InputActionId> All { get; } = new List<InputActionId>
    {
        Advance,
        Cancel,
        Auto,
        History,
        Menu,
        OptionUp,
        OptionDown,
        Confirm,
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
}

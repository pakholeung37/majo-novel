#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Input;

public sealed record InputActionId
{
    public InputActionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Input action ID is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum InputDeviceKind
{
    Unknown,
    Keyboard,
    Mouse,
    Gamepad,
}

public sealed record InputActivation
{
    public InputActivation(InputActionId action, ulong frame, InputDeviceKind device)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Frame = frame;
        Device = device;
    }

    public InputActionId Action { get; }

    public ulong Frame { get; }

    public InputDeviceKind Device { get; }
}

public enum InputHandleKind
{
    Scope,
    Muter,
}

public sealed record InputHandleSnapshot(
    InputHandleKind Kind,
    string? Name,
    bool IsModal,
    string OwnerName,
    IReadOnlyList<string> Actions);

public sealed class InputScopeOptions
{
    private readonly HashSet<InputActionId>? actions;

    public InputScopeOptions(
        IEnumerable<InputActionId>? actions = null,
        bool isModal = false,
        string? name = null)
    {
        var normalizedActions = actions?
            .Select(action =>
                action ?? throw new ArgumentException(
                    "Input actions cannot contain null.",
                    nameof(actions)))
            .Distinct()
            .ToArray();
        this.actions = normalizedActions is null
            ? null
            : new HashSet<InputActionId>(normalizedActions);
        Actions = normalizedActions is null
            ? null
            : Array.AsReadOnly(normalizedActions);
        IsModal = isModal;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public IReadOnlyCollection<InputActionId>? Actions { get; }

    public bool IsModal { get; }

    public string? Name { get; }

    internal bool Accepts(InputActionId action) => actions is null || actions.Contains(action);
}

public sealed class InputMuteOptions
{
    private readonly HashSet<InputActionId>? actions;

    public InputMuteOptions(IEnumerable<InputActionId>? actions = null, string? name = null)
    {
        var normalizedActions = actions?
            .Select(action =>
                action ?? throw new ArgumentException(
                    "Input actions cannot contain null.",
                    nameof(actions)))
            .Distinct()
            .ToArray();
        this.actions = normalizedActions is null
            ? null
            : new HashSet<InputActionId>(normalizedActions);
        Actions = normalizedActions is null
            ? null
            : Array.AsReadOnly(normalizedActions);
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public IReadOnlyCollection<InputActionId>? Actions { get; }

    public string? Name { get; }

    internal bool Mutes(InputActionId action) => actions is null || actions.Contains(action);
}

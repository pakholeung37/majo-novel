#nullable enable

using System;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

public partial class YarnDialogueVariableStore : InMemoryVariableStorage,
    IDialogueVariableStore
{
    public event Action<DialogueVariableChanged>? Changed;

    bool IDialogueVariableStore.Contains(string name)
    {
        var yarnName = ToYarnName(name);
        if (Contains(yarnName))
        {
            return true;
        }

        return Program is not null &&
            ((IDialogueVariableStore)this).TryGet(name, out _);
    }

    bool IDialogueVariableStore.TryGet(string name, out DialogueValue value)
    {
        var yarnName = ToYarnName(name);
        var (numbers, strings, booleans) = GetAllVariables();
        if (numbers.TryGetValue(yarnName, out var storedNumber))
        {
            value = DialogueValue.FromNumber(storedNumber);
            return true;
        }

        if (strings.TryGetValue(yarnName, out var storedText))
        {
            value = DialogueValue.FromString(storedText);
            return true;
        }

        if (booleans.TryGetValue(yarnName, out var storedBoolean))
        {
            value = DialogueValue.FromBoolean(storedBoolean);
            return true;
        }

        if (Program is null)
        {
            value = default;
            return false;
        }

        if (TryGetValue<float>(yarnName, out var number))
        {
            value = DialogueValue.FromNumber(number);
            return true;
        }

        if (TryGetValue<string>(yarnName, out var text))
        {
            value = DialogueValue.FromString(text);
            return true;
        }

        if (TryGetValue<bool>(yarnName, out var boolean))
        {
            value = DialogueValue.FromBoolean(boolean);
            return true;
        }

        value = default;
        return false;
    }

    void IDialogueVariableStore.Set(string name, DialogueValue value)
    {
        var yarnName = ToYarnName(name);
        switch (value.Kind)
        {
            case DialogueValueKind.Number:
                if (value.NumberValue is > float.MaxValue or < float.MinValue)
                {
                    throw new DialogueVariableException(
                        $"Dialogue variable '{name}' exceeds Yarn's number range.");
                }

                SetValue(yarnName, (float)value.NumberValue);
                break;
            case DialogueValueKind.String:
                SetValue(yarnName, value.StringValue!);
                break;
            case DialogueValueKind.Boolean:
                SetValue(yarnName, value.BooleanValue);
                break;
            default:
                throw new DialogueVariableException(
                    $"Unsupported dialogue value kind '{value.Kind}'.");
        }
    }

    void IDialogueVariableStore.Clear() => Clear();

    public override void SetValue(string variableName, float floatValue)
    {
        base.SetValue(variableName, floatValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromNumber(floatValue)));
    }

    public override void SetValue(string variableName, string stringValue)
    {
        base.SetValue(variableName, stringValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromString(stringValue)));
    }

    public override void SetValue(string variableName, bool boolValue)
    {
        base.SetValue(variableName, boolValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromBoolean(boolValue)));
    }

    private static string ToYarnName(string name) => $"${DialogueVariables.NormalizeName(name)}";

    private static string FromYarnName(string name) => name.Length > 0 && name[0] == '$'
        ? name[1..]
        : name;
}

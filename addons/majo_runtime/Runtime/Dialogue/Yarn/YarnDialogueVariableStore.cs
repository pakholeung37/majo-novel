#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YarnSpinnerGodot;
using Majo.Runtime.State;

namespace Majo.Runtime.Dialogue.Yarn;

public partial class YarnDialogueVariableStore : InMemoryVariableStorage,
    IDialogueVariableStore, IStateMutationParticipant
{
    private StateMutationGate? mutationGate;
    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateRuntimeException("Yarn variables already belong to another runtime's state gate.");
        mutationGate = gate;
    }
    private void Write(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }
    public override void Clear() => Write(base.Clear);
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

        if (TryGetFromStorage<float>(yarnName, out var number))
        {
            value = DialogueValue.FromNumber(number);
            return true;
        }

        if (TryGetFromStorage<string>(yarnName, out var text) && text is not null)
        {
            value = DialogueValue.FromString(text);
            return true;
        }

        if (TryGetFromStorage<bool>(yarnName, out var boolean))
        {
            value = DialogueValue.FromBoolean(boolean);
            return true;
        }

        value = default;
        return false;
    }

    private bool TryGetFromStorage<T>(string yarnName, out T? result)
    {
        try
        {
            return TryGetValue<T>(yarnName, out result);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            result = default;
            return false;
        }
    }

    void IDialogueVariableStore.SetValue(string name, DialogueValue value)
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
            case DialogueValueKind.Text:
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

    IReadOnlyDictionary<string, DialogueValue> IDialogueVariableStore.Capture()
    {
        var result = new Dictionary<string, DialogueValue>(StringComparer.Ordinal);
        var (numbers, strings, booleans) = GetAllVariables();
        foreach (var pair in numbers)
        {
            result[FromYarnName(pair.Key)] = DialogueValue.FromNumber(pair.Value);
        }

        foreach (var pair in strings)
        {
            result[FromYarnName(pair.Key)] = DialogueValue.FromString(pair.Value);
        }

        foreach (var pair in booleans)
        {
            result[FromYarnName(pair.Key)] = DialogueValue.FromBoolean(pair.Value);
        }

        return new ReadOnlyDictionary<string, DialogueValue>(result);
    }

    void IDialogueVariableStore.ReplaceAll(
        IReadOnlyDictionary<string, DialogueValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Write(() =>
        {
            Clear();
            foreach (var pair in values)
                ((IDialogueVariableStore)this).SetValue(pair.Key, pair.Value);
        });
    }

    public override void SetValue(string variableName, float floatValue) => Write(() =>
    {
        base.SetValue(variableName, floatValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromNumber(floatValue)));
    });

    public override void SetValue(string variableName, string stringValue) => Write(() =>
    {
        base.SetValue(variableName, stringValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromString(stringValue)));
    });

    public override void SetValue(string variableName, bool boolValue) => Write(() =>
    {
        base.SetValue(variableName, boolValue);
        Changed?.Invoke(new DialogueVariableChanged(
            FromYarnName(variableName),
            DialogueValue.FromBoolean(boolValue)));
    });

    private static string ToYarnName(string name) => $"${DialogueVariables.NormalizeName(name)}";

    private static string FromYarnName(string name) => name.Length > 0 && name[0] == '$'
        ? name[1..]
        : name;
}

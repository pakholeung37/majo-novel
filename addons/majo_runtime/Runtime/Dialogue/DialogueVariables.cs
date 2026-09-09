#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.State;

namespace Majo.Runtime.Dialogue;

public enum DialogueValueKind
{
    Number,
    Text,
    Boolean,
}

public readonly record struct DialogueValue
{
    private DialogueValue(
        DialogueValueKind kind,
        double numberValue,
        string? stringValue,
        bool booleanValue)
    {
        Kind = kind;
        NumberValue = numberValue;
        StringValue = stringValue;
        BooleanValue = booleanValue;
    }

    public DialogueValueKind Kind { get; }

    public double NumberValue { get; }

    public string? StringValue { get; }

    public bool BooleanValue { get; }

    public static DialogueValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be finite.");
        }

        return new DialogueValue(DialogueValueKind.Number, value, default, default);
    }

    public static DialogueValue FromString(string value) => new(
        DialogueValueKind.Text,
        default,
        value ?? throw new ArgumentNullException(nameof(value)),
        default);

    public static DialogueValue FromBoolean(bool value) => new(
        DialogueValueKind.Boolean,
        default,
        default,
        value);
}

public sealed record DialogueVariableChanged(string Name, DialogueValue Value);

public interface IDialogueVariableStore
{
    event Action<DialogueVariableChanged>? Changed;

    bool Contains(string name);

    bool TryGet(string name, out DialogueValue value);

    void SetValue(string name, DialogueValue value);

    void Clear();

    IReadOnlyDictionary<string, DialogueValue> Capture() =>
        throw new NotSupportedException("The variable store does not support snapshots.");

    void ReplaceAll(IReadOnlyDictionary<string, DialogueValue> values) =>
        throw new NotSupportedException("The variable store does not support snapshots.");
}

public interface IDialogueVariables : IRuntimeService
{
    event Action<DialogueVariableChanged>? Changed;

    bool Contains(string name);

    bool TryGet(string name, out DialogueValue value);

    DialogueValue GetValue(string name);

    void SetValue(string name, DialogueValue value);

    void SetNumber(string name, double value);

    void SetString(string name, string value);

    void SetBoolean(string name, bool value);

    IReadOnlyDictionary<string, DialogueValue> Capture();

    void ReplaceAll(IReadOnlyDictionary<string, DialogueValue> values);
}

public sealed class DialogueVariables : IDialogueVariables, IStateMutationParticipant
{
    private StateMutationGate? mutationGate;
    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateRuntimeException("Variables already belong to another runtime's state gate.");
        if (store is IStateMutationParticipant participant) participant.BindStateMutationGate(gate);
        mutationGate = gate;
    }

    private void Write(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }
    private readonly IDialogueVariableStore store;
    private bool isInitialized;

    public DialogueVariables(IDialogueVariableStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event Action<DialogueVariableChanged>? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            store.Changed += HandleChanged;
            isInitialized = true;
        }

        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(store.Clear);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            store.Changed -= HandleChanged;
            Write(store.Clear);
            isInitialized = false;
        }

        return Task.CompletedTask;
    }

    public bool Contains(string name) => store.Contains(NormalizeName(name));

    public bool TryGet(string name, out DialogueValue value) =>
        store.TryGet(NormalizeName(name), out value);

    public DialogueValue GetValue(string name)
    {
        var normalized = NormalizeName(name);
        if (!store.TryGet(normalized, out var value))
        {
            throw new DialogueVariableException(
                $"Dialogue variable '{normalized}' does not exist.");
        }

        return value;
    }

    public void SetValue(string name, DialogueValue value) =>
        Write(() => store.SetValue(NormalizeName(name), value));

    public void SetNumber(string name, double value) =>
        SetValue(name, DialogueValue.FromNumber(value));

    public void SetString(string name, string value) =>
        SetValue(name, DialogueValue.FromString(value));

    public void SetBoolean(string name, bool value) =>
        SetValue(name, DialogueValue.FromBoolean(value));

    public IReadOnlyDictionary<string, DialogueValue> Capture()
    {
        var snapshot = store.Capture().ToDictionary(
            pair => NormalizeName(pair.Key),
            pair => pair.Value,
            StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, DialogueValue>(snapshot);
    }

    public void ReplaceAll(IReadOnlyDictionary<string, DialogueValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.ToDictionary(
            pair => NormalizeName(pair.Key),
            pair => pair.Value,
            StringComparer.Ordinal);
        Write(() => store.ReplaceAll(normalized));
    }

    public static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name[0] == '$' ? name[1..] : name;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Variable name is required.", nameof(name));
        }

        return normalized;
    }

    private void HandleChanged(DialogueVariableChanged change) => Changed?.Invoke(change);
}

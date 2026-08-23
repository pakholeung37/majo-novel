#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue;

public enum DialogueValueKind
{
    Number,
    String,
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
        DialogueValueKind.String,
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

    void Set(string name, DialogueValue value);

    void Clear();
}

public interface IDialogueVariables : IRuntimeService
{
    event Action<DialogueVariableChanged>? Changed;

    bool Contains(string name);

    bool TryGet(string name, out DialogueValue value);

    DialogueValue Get(string name);

    void Set(string name, DialogueValue value);

    void SetNumber(string name, double value);

    void SetString(string name, string value);

    void SetBoolean(string name, bool value);
}

public sealed class DialogueVariables : IDialogueVariables
{
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
        store.Clear();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            store.Changed -= HandleChanged;
            store.Clear();
            isInitialized = false;
        }

        return Task.CompletedTask;
    }

    public bool Contains(string name) => store.Contains(NormalizeName(name));

    public bool TryGet(string name, out DialogueValue value) =>
        store.TryGet(NormalizeName(name), out value);

    public DialogueValue Get(string name)
    {
        var normalized = NormalizeName(name);
        if (!store.TryGet(normalized, out var value))
        {
            throw new DialogueVariableException(
                $"Dialogue variable '{normalized}' does not exist.");
        }

        return value;
    }

    public void Set(string name, DialogueValue value) =>
        store.Set(NormalizeName(name), value);

    public void SetNumber(string name, double value) =>
        Set(name, DialogueValue.FromNumber(value));

    public void SetString(string name, string value) =>
        Set(name, DialogueValue.FromString(value));

    public void SetBoolean(string name, bool value) =>
        Set(name, DialogueValue.FromBoolean(value));

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

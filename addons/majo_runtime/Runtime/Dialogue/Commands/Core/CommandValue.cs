#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;

namespace Majo.Runtime.Dialogue.Commands;

public enum CommandValueOrigin
{
    Unspecified,
    Explicit,
    ContractDefault,
}

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods for CommandValue<T>")]
public readonly record struct CommandValue<T>(
    bool WasProvided,
    T? Value,
    CommandValueOrigin Origin)
{
    public bool HasValue => WasProvided || Origin == CommandValueOrigin.ContractDefault;

    public T GetValueOrDefault(T fallback) => HasValue && Value is not null ? Value : fallback;

    public static CommandValue<T> Unspecified() =>
        new(false, default, CommandValueOrigin.Unspecified);

    public static CommandValue<T> Explicit(T value) =>
        new(true, value, CommandValueOrigin.Explicit);

    public static CommandValue<T> ContractDefault(T value) =>
        new(false, value, CommandValueOrigin.ContractDefault);
}

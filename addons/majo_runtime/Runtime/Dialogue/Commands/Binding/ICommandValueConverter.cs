#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public interface ICommandValueConverter
{
    Type TargetType { get; }

    object? ParseUntyped(string text);
}

public interface ICommandValueConverter<T> : ICommandValueConverter
{
    T Parse(string text);

    Type ICommandValueConverter.TargetType => typeof(T);

    object? ICommandValueConverter.ParseUntyped(string text) => Parse(text);
}

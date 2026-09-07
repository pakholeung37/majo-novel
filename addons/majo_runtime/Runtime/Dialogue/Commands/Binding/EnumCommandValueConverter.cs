#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

public class EnumCommandValueConverter<TEnum> : ICommandValueConverter<TEnum>, ICommandValueDescriptor<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> aliases;
    private readonly bool ignoreCase;
    private readonly CommandValueDescription description;

    public EnumCommandValueConverter(
        IReadOnlyDictionary<string, TEnum>? aliases = null,
        bool ignoreCase = true)
    {
        this.ignoreCase = ignoreCase;
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var dict = new Dictionary<string, TEnum>(comparer);

        if (aliases is not null)
        {
            foreach (var kvp in aliases)
            {
                dict[kvp.Key] = kvp.Value;
            }
        }

        this.aliases = dict;

        var canonical = Enum.GetNames<TEnum>()
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var aliasDict = aliases?.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToString().ToLowerInvariant(),
            comparer);

        description = new CommandValueDescription(
            DisplayName: typeof(TEnum).Name,
            CanonicalValues: canonical,
            Aliases: aliasDict);
    }

    public CommandValueDescription? Description => description;

    public virtual TEnum Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();

        if (long.TryParse(trimmed, out _) || ulong.TryParse(trimmed, out _))
        {
            throw new CommandValueFormatException(
                $"Numeric values are not allowed for enum '{typeof(TEnum).Name}'.");
        }

        if (aliases.TryGetValue(trimmed, out var aliased))
        {
            return aliased;
        }

        if (Enum.TryParse<TEnum>(trimmed, ignoreCase, out var result) && Enum.IsDefined(result))
        {
            return result;
        }

        var valid = string.Join(", ", description.CanonicalValues ?? Array.Empty<string>());
        throw new CommandValueFormatException($"must be one of {valid}");
    }

    public virtual string Format(TEnum value) =>
        value.ToString().ToLowerInvariant();
}

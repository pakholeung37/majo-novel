#nullable enable

using System;
using System.Text.Json;

namespace Majo.Runtime.State;

public static class StatePayloadJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(StateRecord record)
    {
        try
        {
            return record.Payload.Deserialize<T>(Options) ?? throw new StateSnapshotException(
                $"State record '{record.Key}' has an empty payload.");
        }
        catch (StateSnapshotException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StateSnapshotException(
                $"State record '{record.Key}' has an invalid payload.",
                exception);
        }
    }
}

using System.Text.Json;

namespace Majo.Runtime.State;

internal static class StateJson
{
    public static JsonElement Serialize<T>(T value) => StatePayloadJson.Serialize(value);
    public static T Deserialize<T>(StateRecord record) => StatePayloadJson.Deserialize<T>(record);
}

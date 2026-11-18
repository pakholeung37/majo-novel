#nullable enable

using System;
using Majo.Runtime.State;

namespace Majo.Runtime.Save;

public class SaveSlotException : StateRuntimeException
{
    public SaveSlotException(string message) : base(message) { }
    public SaveSlotException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class SaveSlotUnavailableException : SaveSlotException
{
    public SaveSlotUnavailableException(SaveSlotAvailability availability)
        : base("Save slot operation is unavailable: " +
            string.Join(", ", availability.Reasons))
    {
        Availability = availability;
    }

    public SaveSlotAvailability Availability { get; }
}

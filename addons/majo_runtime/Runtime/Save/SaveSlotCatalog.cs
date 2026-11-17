#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.History;
using Majo.Runtime.State;
using static Majo.Runtime.Save.SaveSlotMetadataCodec;

namespace Majo.Runtime.Save;

internal sealed class SaveSlotCatalog
{
    public SaveSlotCatalog(IReadOnlyDictionary<SaveSlotKind, int> capacities)
    {
        ArgumentNullException.ThrowIfNull(capacities);
        Capacities = new ReadOnlyDictionary<SaveSlotKind, int>(Enum.GetValues<SaveSlotKind>().ToDictionary(
            kind => kind, kind => capacities.TryGetValue(kind, out var capacity) && capacity > 0 ? capacity :
                throw new ArgumentException($"A positive capacity is required for slot kind '{kind}'.", nameof(capacities))));
    }
    public IReadOnlyDictionary<SaveSlotKind, int> Capacities { get; }
    public List<SaveSlotUnavailableReason> ValidateSlot(SaveSlotId slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var reasons = new List<SaveSlotUnavailableReason>();
        if (!Capacities.TryGetValue(slot.Kind, out var capacity) || slot.Index > capacity)
        {
            reasons.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.InvalidSlot,
                $"Slot '{slot}' exceeds configured capacity."));
        }
        return reasons;
    }

    public void ThrowIfInvalid(SaveSlotId slot)
    {
        var availability = new SaveSlotAvailability(ValidateSlot(slot));
        if (!availability.IsAllowed)
        {
            throw new SaveSlotUnavailableException(availability);
        }
    }

}

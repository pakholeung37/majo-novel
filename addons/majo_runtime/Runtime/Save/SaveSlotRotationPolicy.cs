#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Save;

public static class SaveSlotRotationPolicy
{
    public static SaveSlotId DetermineNextRotatingSlot(
        SaveSlotKind kind,
        int capacity,
        IReadOnlyList<SaveSlotMetadata> existingSlots)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        var used = existingSlots.Select(value => value.Id.Index).ToHashSet();
        var free = Enumerable.Range(1, capacity)
            .FirstOrDefault(index => !used.Contains(index));

        if (free > 0)
        {
            return new SaveSlotId(kind, free);
        }

        return existingSlots
            .OrderBy(value => value.SavedAt ?? DateTimeOffset.MinValue)
            .ThenBy(value => value.Id.Index)
            .First().Id;
    }
}

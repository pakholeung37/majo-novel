#nullable enable

using System.Collections.ObjectModel;
using Majo.Runtime.Save;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SaveSlotRotationPolicyTests
{
    [Fact]
    public void DetermineNextRotatingSlot_EmptyExistingSlots_SelectsSlot1()
    {
        var existing = Array.Empty<SaveSlotMetadata>();
        var result = SaveSlotRotationPolicy.DetermineNextRotatingSlot(SaveSlotKind.Quick, 3, existing);

        Assert.Equal(new SaveSlotId(SaveSlotKind.Quick, 1), result);
    }

    [Fact]
    public void DetermineNextRotatingSlot_Slot1Occupied_SelectsSlot2()
    {
        var existing = new[]
        {
            CreateSlotMetadata(SaveSlotKind.Quick, 1, DateTimeOffset.UtcNow)
        };

        var result = SaveSlotRotationPolicy.DetermineNextRotatingSlot(SaveSlotKind.Quick, 3, existing);
        Assert.Equal(new SaveSlotId(SaveSlotKind.Quick, 2), result);
    }

    [Fact]
    public void DetermineNextRotatingSlot_AllOccupied_SelectsOldestSlot()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-30); // oldest!

        var existing = new[]
        {
            CreateSlotMetadata(SaveSlotKind.Quick, 1, t0),
            CreateSlotMetadata(SaveSlotKind.Quick, 2, t1),
            CreateSlotMetadata(SaveSlotKind.Quick, 3, t2),
        };

        var result = SaveSlotRotationPolicy.DetermineNextRotatingSlot(SaveSlotKind.Quick, 3, existing);
        Assert.Equal(new SaveSlotId(SaveSlotKind.Quick, 3), result);
    }

    private static SaveSlotMetadata CreateSlotMetadata(SaveSlotKind kind, int index, DateTimeOffset savedAt) =>
        new(
            new SaveSlotId(kind, index),
            SaveSlotCompatibility.Compatible,
            savedAt,
            "build1",
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
}

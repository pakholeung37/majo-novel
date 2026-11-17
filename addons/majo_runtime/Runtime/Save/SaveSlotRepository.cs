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

internal sealed class SaveSlotRepository(
    IStateSerializer serializer, IStateStorage storage, SaveSlotCatalog catalog, string storageNamespace)
{
    public Task<byte[]> ReadAsync(SaveSlotId slot, CancellationToken token) => storage.ReadAsync(ToKey(slot), token);
    public Task WriteAsync(SaveSlotId slot, byte[] bytes, CancellationToken token) => storage.WriteAsync(ToKey(slot), bytes, token);
    public Task DeleteAsync(SaveSlotId slot, CancellationToken token) => storage.DeleteAsync(ToKey(slot), token);
    public byte[] Serialize(StateEnvelope envelope) => serializer.Serialize(envelope);

    public async Task<IReadOnlyList<SaveSlotMetadata>> ListAsync(
        SaveSlotKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is not null && !Enum.IsDefined(kind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var keys = await storage.ListAsync(cancellationToken);
        var results = new List<SaveSlotMetadata>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseKey(key, out var slot) ||
                (kind is not null && slot.Kind != kind.Value) ||
                catalog.ValidateSlot(slot).Count > 0)
            {
                continue;
            }
            results.Add(await ReadMetadataCoreAsync(slot, cancellationToken));
        }

        return results
            .OrderBy(value => value.Id.Kind)
            .ThenByDescending(value => value.SavedAt)
            .ThenBy(value => value.Id.Index)
            .ToArray();
    }

    public async Task<SaveSlotMetadata> ReadMetadataCoreAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken)
    {
        var bytes = await storage.ReadAsync(ToKey(slot), cancellationToken);
        try
        {
            var envelope = DeserializeCompatible(slot, bytes);
            return ToMetadata(slot, envelope);
        }
        catch (StateCompatibilityException exception)
        {
            return UnavailableMetadata(slot, SaveSlotCompatibility.Incompatible, exception.Message);
        }
        catch (StateRuntimeException exception)
        {
            return UnavailableMetadata(slot, SaveSlotCompatibility.Corrupt, exception.Message);
        }
    }

    public async Task<SaveSlotMetadata?> ReadMetadataOrNullAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadMetadataCoreAsync(slot, cancellationToken);
        }
        catch (StateStorageNotFoundException)
        {
            return null;
        }
    }

    public StateEnvelope DeserializeCompatible(SaveSlotId slot, ReadOnlySpan<byte> bytes)
    {
        var envelope = serializer.Deserialize(bytes);
        if (envelope.Scope != StateScope.Game)
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' contains scope '{envelope.Scope}', expected Game.");
        }
        if (!string.Equals(envelope.StorageNamespace, storageNamespace, StringComparison.Ordinal))
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' belongs to namespace '{envelope.StorageNamespace}'.");
        }
        if (!MetadataMatchesSlot(envelope.DisplayMetadata, slot))
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' metadata does not match its logical key.");
        }
        return envelope;
    }

}

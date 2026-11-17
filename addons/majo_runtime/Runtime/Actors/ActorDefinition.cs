#nullable enable

using System;

namespace Majo.Runtime.Actors;

public class ActorDefinition
{
    public ActorDefinition(
        string id,
        string factoryId,
        object? factoryData = null,
        string? definitionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        if (id is "move" or "fx" or "hide")
            throw new ActorDefinitionException(id, "This instance id is reserved by Direction child commands.");
        Id = id;
        DefinitionId = string.IsNullOrWhiteSpace(definitionId) ? id : definitionId.Trim();
        FactoryId = factoryId;
        FactoryData = factoryData;
    }

    public string Id { get; }

    public string DefinitionId { get; }

    public string FactoryId { get; }

    public object? FactoryData { get; }

    public TData GetFactoryData<TData>() where TData : class =>
        FactoryData as TData ?? throw new ActorDefinitionException(
            Id,
            $"Factory '{FactoryId}' requires definition data '{typeof(TData).FullName}'.");

    public bool TryGetFactoryData<TData>(out TData? data) where TData : class
    {
        data = FactoryData as TData;
        return data is not null;
    }

}

public enum CharacterPositionPreset
{
    Left,
    Center,
    Right,
}

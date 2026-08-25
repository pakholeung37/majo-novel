using System;

namespace Majo.Runtime.Operations;

public enum OperationTargetKind
{
    Id,
    Group,
}

public readonly record struct OperationTarget
{
    public OperationTarget(OperationTargetKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Kind = kind;
        Name = name;
    }

    public OperationTargetKind Kind { get; }

    public string Name { get; }

    public static OperationTarget ForId(string id) => new(OperationTargetKind.Id, id);

    public static OperationTarget ForGroup(string groupId) =>
        new(OperationTargetKind.Group, groupId);

    public override string ToString() => $"{Kind}:{Name}";
}

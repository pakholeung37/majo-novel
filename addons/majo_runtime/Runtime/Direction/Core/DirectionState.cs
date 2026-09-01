#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Direction.Core;

public readonly record struct DirectionPoint(float X, float Y)
{
    public DirectionPoint Validate()
    {
        if (!float.IsFinite(X) || !float.IsFinite(Y))
            throw new ArgumentException("Direction coordinates must be finite.");
        return this;
    }
}

public readonly record struct DirectionColor(float R, float G, float B, float A = 1)
{
    public static DirectionColor White { get; } = new(1, 1, 1, 1);
    public static DirectionColor Black { get; } = new(0, 0, 0, 1);

    public DirectionColor Validate()
    {
        if (!float.IsFinite(R) || !float.IsFinite(G) ||
            !float.IsFinite(B) || !float.IsFinite(A) ||
            R is < 0 or > 1 || G is < 0 or > 1 ||
            B is < 0 or > 1 || A is < 0 or > 1)
        {
            throw new ArgumentException("Direction color channels must be between zero and one.");
        }

        return this;
    }
}

public enum BackdropFit
{
    Cover,
    Contain,
    Stretch,
}

public sealed record BackdropState(
    ResourceKey Resource,
    BackdropFit Fit,
    DirectionColor Tint)
{
    public BackdropState(ResourceKey resource)
        : this(resource, BackdropFit.Cover, DirectionColor.White)
    {
    }

    public BackdropState Validate()
    {
        if (!Enum.IsDefined(Fit)) throw new ArgumentOutOfRangeException(nameof(Fit));
        Tint.Validate();
        return this;
    }
}

public sealed record ActorState(
    ActorId Id,
    DirectionPoint Position,
    string? Appearance,
    float Scale,
    int Z)
{
    public static ActorState CreateDefault(ActorId id) =>
        new(id, new DirectionPoint(50, 0), null, 1, 0);

    public ActorState Validate()
    {
        Position.Validate();
        if (!float.IsFinite(Scale) || Scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(Scale));
        if (Appearance is not null && string.IsNullOrWhiteSpace(Appearance))
            throw new ArgumentException("Actor appearance cannot be blank.", nameof(Appearance));
        return this;
    }
}

public sealed record StageEffectState(DirectionColor Tint)
{
    public static StageEffectState Default { get; } = new(DirectionColor.White);
}

public sealed record StageState(
    StageId Id,
    BackdropState? Backdrop,
    IReadOnlyDictionary<ActorId, ActorState> Actors,
    StageEffectState Effects)
{
    public static StageState Create(StageId id) =>
        new(id, null, DirectionCollections.Empty<ActorId, ActorState>(), StageEffectState.Default);
}

public abstract record ViewSource;

public sealed record StageViewSource(StageId StageId) : ViewSource;

public sealed record CgViewSource(ResourceKey Resource) : ViewSource;

public sealed record ViewPose(DirectionPoint Center, float Zoom, float RotationDegrees)
{
    public static ViewPose Default { get; } = new(new DirectionPoint(50, 50), 1, 0);

    public ViewPose Validate()
    {
        Center.Validate();
        if (!float.IsFinite(Zoom) || Zoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(Zoom));
        if (!float.IsFinite(RotationDegrees))
            throw new ArgumentOutOfRangeException(nameof(RotationDegrees));
        return this;
    }
}

public sealed record ViewState(ViewId Id, ViewSource Source, ViewPose Pose);

public sealed record ScreenState(
    LayoutId Layout,
    IReadOnlyDictionary<LayoutSlotId, ViewId> Bindings)
{
    public static ScreenState Default { get; } = new(
        LayoutId.Single,
        DirectionCollections.Freeze(new Dictionary<LayoutSlotId, ViewId>
        {
            [LayoutSlotId.Main] = ViewId.Main,
        }));
}

public static class DirectionLayoutCatalog
{
    public static IReadOnlySet<LayoutSlotId> RequiredSlots(LayoutId layout) =>
        layout.Value.ToLowerInvariant() switch
        {
            "single" => new HashSet<LayoutSlotId> { LayoutSlotId.Main },
            "split" => new HashSet<LayoutSlotId>
            {
                LayoutSlotId.Main,
                new LayoutSlotId("inset"),
            },
            "pip" => new HashSet<LayoutSlotId>
            {
                LayoutSlotId.Main,
                new LayoutSlotId("inset"),
            },
            _ => throw new DirectionReferenceException(
                $"Unknown Direction layout '{layout}'."),
        };
}

public sealed record CurtainState(DirectionColor Color, float Opacity)
{
    public static CurtainState Open { get; } = new(DirectionColor.Black, 0);

    public CurtainState Validate()
    {
        Color.Validate();
        if (!float.IsFinite(Opacity) || Opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Opacity));
        return this;
    }
}

public sealed record StableEffectState(CurtainState Curtain)
{
    public static StableEffectState Default { get; } = new(CurtainState.Open);
}

public sealed record DirectionState(
    IReadOnlyDictionary<StageId, StageState> Stages,
    IReadOnlyDictionary<ViewId, ViewState> Views,
    ScreenState Screen,
    StableEffectState Effects,
    long Revision)
{
    public static DirectionState CreateDefault() => new(
        DirectionCollections.Freeze(new Dictionary<StageId, StageState>
        {
            [StageId.Main] = StageState.Create(StageId.Main),
        }),
        DirectionCollections.Freeze(new Dictionary<ViewId, ViewState>
        {
            [ViewId.Main] = new(
                ViewId.Main,
                new StageViewSource(StageId.Main),
                ViewPose.Default),
        }),
        ScreenState.Default,
        StableEffectState.Default,
        0);
}

internal static class DirectionCollections
{
    public static IReadOnlyDictionary<TKey, TValue> Empty<TKey, TValue>()
        where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(
            new Dictionary<TKey, TValue>());

    public static IReadOnlyDictionary<TKey, TValue> Freeze<TKey, TValue>(
        IDictionary<TKey, TValue> values)
        where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(
            new Dictionary<TKey, TValue>(values));
}

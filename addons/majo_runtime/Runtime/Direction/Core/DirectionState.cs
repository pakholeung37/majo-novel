#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

public enum DirectionActorKind { Character, SceneObject }

public sealed record ActorState(
    ActorId Id,
    DirectionPoint Position,
    string? Appearance,
    float Scale,
    int Z)
{
    public DirectionActorKind Kind { get; init; } = DirectionActorKind.Character;
    public string? DefinitionId { get; init; }
    public bool Visible { get; init; } = true;
    public float RotationDegrees { get; init; }

    public static ActorState CreateDefault(ActorId id) =>
        new(id, new DirectionPoint(50, 0), null, 1, 0);

    public ActorState Validate()
    {
        Position.Validate();
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (Kind == DirectionActorKind.SceneObject && string.IsNullOrWhiteSpace(DefinitionId))
            throw new ArgumentException("Object actors require a definition id.", nameof(DefinitionId));
        if (DefinitionId is not null && string.IsNullOrWhiteSpace(DefinitionId))
            throw new ArgumentException("Actor definition id cannot be blank.", nameof(DefinitionId));
        if (!float.IsFinite(RotationDegrees)) throw new ArgumentOutOfRangeException(nameof(RotationDegrees));
        Majo.Runtime.Actors.ActorZIndexRange.Validate(Z);
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

public sealed record ViewState(
    ViewId Id,
    StageId StageId,
    ViewPose Pose);

public sealed record ScreenState(
    LayoutId Layout,
    IReadOnlyDictionary<LayoutSlotId, ViewId> Bindings)
{
    public static ScreenState Default { get; } = new(
        LayoutId.SingleLayout,
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

public sealed record DirectionSceneContext(string? ProjectId, string? SceneId, long Epoch);

public sealed record DirectionState(
    IReadOnlyDictionary<StageId, StageState> Stages,
    IReadOnlyDictionary<ViewId, ViewState> Views,
    ScreenState Screen,
    StableEffectState Effects,
    long Revision)
{
    public DirectionSceneContext Scene { get; init; } = new(null, null, 0);

    public static DirectionState CreateDefault() => new(
        DirectionCollections.Freeze(new Dictionary<StageId, StageState>
        {
            [StageId.Main] = StageState.Create(StageId.Main),
        }),
        DirectionCollections.Freeze(new Dictionary<ViewId, ViewState>
        {
            [ViewId.Main] = new(
                ViewId.Main,
                StageId.Main,
                ViewPose.Default),
        }),
        ScreenState.Default,
        StableEffectState.Default,
        0);

    public DirectionState Validate()
    {
        if (Stages is null || Views is null || Screen is null || Effects is null)
            throw new DirectionException("Direction state is incomplete.");
        if (Scene is null || Scene.Epoch < 0)
            throw new DirectionException("Direction scene context is invalid.");
        if (Revision < 0 || !Stages.ContainsKey(StageId.Main) || !Views.ContainsKey(ViewId.Main))
            throw new DirectionException("Direction state has no valid main Stage/View.");

        foreach (var pair in Stages)
        {
            if (string.IsNullOrWhiteSpace(pair.Key.Value) || pair.Value is null ||
                pair.Value.Id != pair.Key || pair.Value.Actors is null ||
                pair.Value.Effects is null)
                throw new DirectionException($"Direction state has an invalid Stage '{pair.Key}'.");
            pair.Value.Backdrop?.Validate();
            pair.Value.Effects.Tint.Validate();
            foreach (var actor in pair.Value.Actors)
            {
                if (string.IsNullOrWhiteSpace(actor.Key.Value) || actor.Value is null ||
                    actor.Value.Id != actor.Key)
                    throw new DirectionException($"Direction state has an invalid Actor '{actor.Key}'.");
                actor.Value.Validate();
            }
        }

        foreach (var pair in Views)
        {
            if (string.IsNullOrWhiteSpace(pair.Key.Value) || pair.Value is null ||
                pair.Value.Id != pair.Key)
                throw new DirectionException($"Direction state has an invalid View '{pair.Key}'.");
            pair.Value.Pose.Validate();
            if (string.IsNullOrWhiteSpace(pair.Value.StageId.Value) ||
                !Stages.ContainsKey(pair.Value.StageId))
                throw new DirectionReferenceException(
                    $"View '{pair.Key}' references unknown Stage '{pair.Value.StageId}'.");
        }

        if (string.IsNullOrWhiteSpace(Screen.Layout.Value) || Screen.Bindings is null)
            throw new DirectionException("Direction screen is incomplete.");
        var requiredSlots = DirectionLayoutCatalog.RequiredSlots(Screen.Layout);
        if (
            requiredSlots.Any(slot => !Screen.Bindings.ContainsKey(slot)) ||
            Screen.Bindings.Keys.Any(slot => !requiredSlots.Contains(slot)) ||
            Screen.Bindings.Keys.Any(slot => string.IsNullOrWhiteSpace(slot.Value)) ||
            Screen.Bindings.Values.Any(view => string.IsNullOrWhiteSpace(view.Value) || !Views.ContainsKey(view)))
            throw new DirectionReferenceException("Direction screen bindings do not match its layout.");
        if (Effects.Curtain is null)
            throw new DirectionException("Direction state has incomplete stable effects.");
        Effects.Curtain.Validate();
        return this;
    }
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

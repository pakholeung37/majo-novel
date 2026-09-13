#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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

public sealed record BackdropPlacement(DirectionPoint At, DirectionPoint Origin, float Scale)
{
    public BackdropPlacement Validate()
    {
        At.Validate(); Origin.Validate();
        if (Origin.X is < 0 or > 1 || Origin.Y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Origin), "Backdrop origin uses normalized texture coordinates.");
        if (!float.IsFinite(Scale) || Scale <= 0) throw new ArgumentOutOfRangeException(nameof(Scale));
        return this;
    }
}

public sealed record BackdropState(
    string DefinitionId,
    string FactoryId,
    string Appearance,
    BackdropFit Fit,
    DirectionColor Tint)
{
    public BackdropPlacement? Placement { get; init; }
    /// <summary>Relative distance from the subject plane. One means coplanar.</summary>
    public float Distance { get; init; } = 1;

    public BackdropState(string definitionId, string factoryId, string appearance)
        : this(definitionId, factoryId, appearance, BackdropFit.Cover, DirectionColor.White)
    {
    }

    public BackdropState Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(FactoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Appearance);
        if (DefinitionId.Equals("fx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backdrop definition 'fx' is reserved by the backdrop fx command.");
        if (!Enum.IsDefined(Fit)) throw new ArgumentOutOfRangeException(nameof(Fit));
        Tint.Validate();
        Placement?.Validate();
        if (!float.IsFinite(Distance) || Distance is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Distance), "Backdrop distance must be in [1, 100].");
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

    /// <summary>Normalized camera displacement; positive moves toward the subject plane.</summary>
    public float Dolly { get; init; }
    public DirectionPoint BackgroundOffset { get; init; }
    public float BackgroundScale { get; init; } = 1;

    public ViewPose Validate()
    {
        Center.Validate();
        BackgroundOffset.Validate();
        if (!float.IsFinite(BackgroundScale) || BackgroundScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(BackgroundScale));
        if (!float.IsFinite(Zoom) || Zoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(Zoom));
        if (!float.IsFinite(RotationDegrees))
            throw new ArgumentOutOfRangeException(nameof(RotationDegrees));
        if (!float.IsFinite(Dolly) || Dolly is < -2 or > 0.85f)
            throw new ArgumentOutOfRangeException(nameof(Dolly), "Camera dolly must be in [-2, 0.85].");
        return this;
    }
}

public sealed record ViewParallaxState(
    float Strength,
    DirectionPoint Anchor,
    DirectionPoint LimitPercent)
{
    public static ViewParallaxState Off { get; } = new(0, new DirectionPoint(50, 50), new DirectionPoint(0, 0));
    public static ViewParallaxState Depth { get; } = Off with { UseDepthProjection = true };
    // False retains the pre-v5 projection for old scripts and snapshots.
    public bool UseDepthProjection { get; init; }

    public ViewParallaxState Validate()
    {
        Anchor.Validate();
        LimitPercent.Validate();
        if (!float.IsFinite(Strength) || Strength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Strength), "Parallax strength must be in [0, 1].");
        if (LimitPercent.X < 0 || LimitPercent.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(LimitPercent), "Parallax limits cannot be negative.");
        return this;
    }
}

public sealed record ViewIdleState(
    DirectionPoint AmplitudePercent,
    double PeriodSeconds,
    double PhaseRadians = 0)
{
    public static ViewIdleState Off { get; } = new(new DirectionPoint(0, 0), 12, 0);
    public bool IsEnabled => AmplitudePercent.X != 0 || AmplitudePercent.Y != 0;

    public ViewIdleState Validate()
    {
        AmplitudePercent.Validate();
        if (AmplitudePercent.X < 0 || AmplitudePercent.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(AmplitudePercent), "Idle amplitude cannot be negative.");
        if (!double.IsFinite(PeriodSeconds) || PeriodSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(PeriodSeconds));
        if (!double.IsFinite(PhaseRadians))
            throw new ArgumentOutOfRangeException(nameof(PhaseRadians));
        return this;
    }
}

public sealed record ViewState(
    ViewId Id,
    StageId StageId,
    ViewPose Pose)
{
    public ViewParallaxState Parallax { get; init; } = ViewParallaxState.Depth;
    public ViewIdleState Idle { get; init; } = ViewIdleState.Off;
}

public sealed record SplitLayout(float Divider = 50, float Angle = 0,
    DirectionPoint? MainAt = null, DirectionPoint? InsetAt = null)
{
    public SplitLayout Validate()
    {
        if (!float.IsFinite(Divider) || Divider < 0 || Divider > 100)
            throw new ArgumentOutOfRangeException(nameof(Divider));
        if (!float.IsFinite(Angle) || Angle <= -90 || Angle >= 90)
            throw new ArgumentOutOfRangeException(nameof(Angle));
        foreach (var point in new[] { MainAt, InsetAt })
            if (point is { } value && (!float.IsFinite(value.X) || !float.IsFinite(value.Y)))
                throw new ArgumentOutOfRangeException(nameof(MainAt));
        return this;
    }
}

public sealed record ScreenState(
    LayoutId Layout,
    IReadOnlyDictionary<LayoutSlotId, ViewId> Bindings)
{
    public SplitLayout? Split { get; init; }

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
            pair.Value.Parallax.Validate();
            pair.Value.Idle.Validate();
            if (string.IsNullOrWhiteSpace(pair.Value.StageId.Value) ||
                !Stages.ContainsKey(pair.Value.StageId))
                throw new DirectionReferenceException(
                    $"View '{pair.Key}' references unknown Stage '{pair.Value.StageId}'.");
        }

        if (string.IsNullOrWhiteSpace(Screen.Layout.Value) || Screen.Bindings is null)
            throw new DirectionException("Direction screen is incomplete.");
        Screen.Split?.Validate();
        if (Screen.Split is not null && !Screen.Layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase))
            throw new DirectionException("Split parameters require the split layout.");
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

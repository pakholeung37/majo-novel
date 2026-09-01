#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class DirectionCommandModule : IRuntimeCommandModule
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionResourceKeyResolver resources;
    private readonly IDirectionPositionResolver positions;
    private readonly IDirectionScopeResolver scopes;
    private readonly Func<string?> currentNode;

    public DirectionCommandModule(
        IDirectionBeatRuntime beats,
        IDirectionResourceKeyResolver resources,
        IDirectionPositionResolver positions,
        IDirectionScopeResolver scopes,
        Func<string?> currentNode)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.currentNode = currentNode ?? throw new ArgumentNullException(nameof(currentNode));
    }

    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register<BackdropDirectionCommandArguments>(BackdropAsync)
            .Register<PlaceDirectionCommandArguments>(PlaceAsync)
            .Register<LeaveDirectionCommandArguments>(LeaveAsync)
            .Register<ShotPoseDirectionCommandArguments>(ShotPoseAsync)
            .Register<ShotCgDirectionCommandArguments>(ShotCgAsync)
            .Register<ShotUseDirectionCommandArguments>(ShotUseAsync)
            .Register<LayoutDirectionCommandArguments>(LayoutAsync)
            .Register<BeatBeginDirectionCommandArguments>(BeatBeginAsync)
            .Register<BeatEndDirectionCommandArguments>(BeatEndAsync)
            .Register<BeatJoinDirectionCommandArguments>(BeatJoinAsync)
            .Register<BeatFinishDirectionCommandArguments>(BeatFinishAsync)
            .Register<BeatAbortDirectionCommandArguments>(BeatAbortAsync);
    }

    private Task BackdropAsync(
        BackdropDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("backdrop", async () =>
    {
        await beats.SubmitAsync(
            new BackdropPatch(
                Stage(a.Stage),
                new BackdropState(resources.ResolveBackdrop(a.Resource)),
                Transition(
                    a,
                    c,
                    DirectionTransitionKind.Cut,
                    DirectionTransitionKind.Fade,
                    DirectionTransitionKind.Dissolve)),
            a.Join,
            c.CancellationToken);
    });

    private Task PlaceAsync(
        PlaceDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("place", async () =>
    {
        await beats.SubmitAsync(
            new ActorPatch(
                Stage(a.Stage),
                new ActorId(a.Actor),
                a.At is null
                    ? default
                    : DirectionValue<DirectionPoint>.From(positions.Resolve(a.At)),
                a.Look is null
                    ? default
                    : DirectionValue<string?>.From(a.Look),
                a.Scale is null
                    ? default
                    : DirectionValue<float>.From((float)a.Scale.Value),
                a.Z is null ? default : DirectionValue<int>.From(a.Z.Value),
                Transition(
                    a,
                    c,
                    DirectionTransitionKind.Cut,
                    DirectionTransitionKind.Move,
                    DirectionTransitionKind.Fade)),
            a.Join,
            c.CancellationToken);
    });

    private Task LeaveAsync(
        LeaveDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("leave", async () =>
    {
        await beats.SubmitAsync(
            new ActorLeavePatch(
                Stage(a.Stage),
                new ActorId(a.Actor),
                Transition(
                    a,
                    c,
                    DirectionTransitionKind.Cut,
                    DirectionTransitionKind.Fade)),
            a.Join,
            c.CancellationToken);
    });

    private Task ShotPoseAsync(
        ShotPoseDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("shot pose", async () =>
    {
        var patch = new ViewPatch(
                View(a.View),
                a.Stage is null
                    ? default
                    : DirectionValue<ViewSource>.From(
                        new StageViewSource(Stage(a.Stage))),
                a.Center is null
                    ? default
                    : DirectionValue<DirectionPoint>.From(
                        new DirectionPoint(a.Center.Value.X, a.Center.Value.Y)),
                a.Zoom is null
                    ? default
                    : DirectionValue<float>.From((float)a.Zoom.Value),
                a.Rotation is null
                    ? default
                    : DirectionValue<float>.From((float)a.Rotation.Value),
                Transition(a, c, Enum.GetValues<DirectionTransitionKind>()));
        if (a.As is not null)
            scopes.DefineShot(RequireNode(), a.As, patch);
        await beats.SubmitAsync(
            patch,
            a.Join,
            c.CancellationToken);
    });

    private Task ShotUseAsync(
        ShotUseDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("shot use", async () =>
    {
        var patch = scopes.ResolveShot(RequireNode(), a.Alias);
        if (a.View is not null)
            patch = patch with { ViewId = View(a.View) };
        patch = patch with { Transition = Transition(a, c, Enum.GetValues<DirectionTransitionKind>()) };
        await beats.SubmitAsync(patch, a.Join, c.CancellationToken);
    });

    private Task ShotCgAsync(
        ShotCgDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("shot cg", async () =>
    {
        await beats.SubmitAsync(
            new ViewPatch(
                View(a.View),
                DirectionValue<ViewSource>.From(
                    new CgViewSource(resources.ResolveCg(a.Resource))),
                a.Center is null
                    ? default
                    : DirectionValue<DirectionPoint>.From(
                        new DirectionPoint(a.Center.Value.X, a.Center.Value.Y)),
                a.Zoom is null
                    ? default
                    : DirectionValue<float>.From((float)a.Zoom.Value),
                a.Rotation is null
                    ? default
                    : DirectionValue<float>.From((float)a.Rotation.Value),
                Transition(a, c, Enum.GetValues<DirectionTransitionKind>())),
            a.Join,
            c.CancellationToken);
    });

    private Task LayoutAsync(
        LayoutDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("layout", async () =>
    {
        var bindings = new Dictionary<LayoutSlotId, ViewId>();
        foreach (var pair in a.Bindings)
            bindings.Add(new LayoutSlotId(pair.Key), View(pair.Value));
        await beats.SubmitAsync(
            new ScreenPatch(
                new LayoutId(a.Layout),
                bindings,
                Transition(a, c, DirectionTransitionKind.Cut, DirectionTransitionKind.Dissolve)),
            a.Join,
            c.CancellationToken);
    });

    private Task BeatBeginAsync(
        BeatBeginDirectionCommandArguments a,
        RuntimeCommandContext _) => ExecuteAsync("beat begin", () =>
    {
        var node = currentNode();
        if (string.IsNullOrWhiteSpace(node))
            throw new DirectionBeatException("Cannot begin a Beat outside a Yarn Node.");
        beats.Begin(
            a.Id,
            node,
            new DirectionBeatDefaults(a.Over, ParseEase(a.Ease)));
        return Task.CompletedTask;
    });

    private Task BeatEndAsync(
        BeatEndDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("beat end", async () =>
    {
        await beats.EndAsync(a.Join, c.CancellationToken);
    });

    private Task BeatJoinAsync(
        BeatJoinDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("beat join", () =>
        beats.JoinAsync(a.Id, c.CancellationToken));

    private Task BeatFinishAsync(
        BeatFinishDirectionCommandArguments a,
        RuntimeCommandContext c) => ExecuteAsync("beat finish", () =>
        beats.FinishAsync(a.Id, c.CancellationToken));

    private Task BeatAbortAsync(
        BeatAbortDirectionCommandArguments _,
        RuntimeCommandContext __) => ExecuteAsync("beat abort", () =>
    {
        beats.AbortOpen();
        return Task.CompletedTask;
    });

    private DirectionTransitionSpec Transition(
        DirectionTransitionCommandArguments a,
        RuntimeCommandContext context,
        params DirectionTransitionKind[] allowed)
    {
        var kind = a.Via ?? DirectionTransitionKind.Cut;
        if (Array.IndexOf(allowed, kind) < 0)
        {
            throw new DirectionException(
                $"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by this command.");
        }
        var defaults = beats.OpenBeatDefaults;
        var duration = a.Over ?? (kind == DirectionTransitionKind.Cut
            ? 0
            : defaults?.DurationSeconds ??
                context.Binding?.DefaultAnimationDurationSeconds ?? 0);
        var ease = ParseEase(a.Ease) ?? defaults?.Ease ?? DirectionEase.Linear;
        return new DirectionTransitionSpec(kind, duration, ease).Validate();
    }

    private static DirectionEase? ParseEase(string? value)
    {
        if (value is null) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "linear" => DirectionEase.Linear,
            "sine-in" or "sinein" => DirectionEase.SineIn,
            "sine-out" or "sineout" => DirectionEase.SineOut,
            "sine-in-out" or "sineinout" => DirectionEase.SineInOut,
            "cubic-in" or "cubicin" => DirectionEase.CubicIn,
            "cubic-out" or "cubicout" => DirectionEase.CubicOut,
            "cubic-in-out" or "cubicinout" => DirectionEase.CubicInOut,
            _ => throw new ArgumentException(
                $"Unsupported Direction ease '{value}'."),
        };
    }

    private StageId Stage(string value) => scopes.ResolveStage(RequireNode(), value);

    private ViewId View(string value) => scopes.ResolveView(RequireNode(), value);

    private string RequireNode()
    {
        var node = currentNode();
        if (string.IsNullOrWhiteSpace(node))
            throw new DirectionReferenceException(
                "Direction symbols cannot be resolved outside a Yarn Node.");
        return node;
    }

    private static async Task ExecuteAsync(string command, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DirectionException or ArgumentException)
        {
            throw new RuntimeCommandException(command, exception.Message, exception);
        }
    }
}

public abstract class DirectionTransitionCommandArguments
{
    [CommandParameter("via", 20)] public DirectionTransitionKind? Via { get; set; }
    [CommandParameter("over", 21, Minimum = 0)] public double? Over { get; set; }
    [CommandParameter("ease", 22)] public string? Ease { get; set; }
    [CommandParameter("join", 23, DefaultValue = "commit")]
    public DirectionCompletionBarrier Join { get; set; }
}

[RuntimeCommand("backdrop", Category = "Direction", Usage = "backdrop <resource> [stage:<id>] [via:<kind>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
public sealed class BackdropDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("resource", 0, Primary = true, Required = true)]
    public string Resource { get; set; } = string.Empty;
    [CommandParameter("stage", 1, DefaultValue = "main")]
    public string Stage { get; set; } = "main";
}

[RuntimeCommand("place", Category = "Direction", Usage = "place <actor> [stage:<id>] [at:<mark|x,y>] [look:<appearance>] [scale:<number>] [z:<integer>] [via:<kind>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "at", "look", "scale", "z")]
public sealed class PlaceDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("actor", 0, Primary = true, Required = true)]
    public string Actor { get; set; } = string.Empty;
    [CommandParameter("stage", 1, DefaultValue = "main")]
    public string Stage { get; set; } = "main";
    [CommandParameter("at", 2)] public string? At { get; set; }
    [CommandParameter("look", 3)] public string? Look { get; set; }
    [CommandParameter("scale", 4, Minimum = 0.001)] public double? Scale { get; set; }
    [CommandParameter("z", 5)] public int? Z { get; set; }
}

[RuntimeCommand("leave", Category = "Direction", Usage = "leave <actor> [stage:<id>] [via:<cut|fade>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
public sealed class LeaveDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("actor", 0, Primary = true, Required = true)]
    public string Actor { get; set; } = string.Empty;
    [CommandParameter("stage", 1, DefaultValue = "main")]
    public string Stage { get; set; } = "main";
}

[RuntimeCommand("shot", "pose", Category = "Direction", Usage = "shot pose [stage:<id>] [view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [as:<alias>] [via:<kind>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "stage", "center", "zoom", "rotation", "as")]
public sealed class ShotPoseDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("stage", 0)] public string? Stage { get; set; }
    [CommandParameter("view", 1, DefaultValue = "main")]
    public string View { get; set; } = "main";
    [CommandParameter("center", 2)] public Vector2? Center { get; set; }
    [CommandParameter("zoom", 3, Minimum = 0.001)] public double? Zoom { get; set; }
    [CommandParameter("rotation", 4)] public double? Rotation { get; set; }
    [CommandParameter("as", 5)] public string? As { get; set; }
}

[RuntimeCommand("shot", "cg", Category = "Direction", Usage = "shot cg <resource> [view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [via:<kind>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
public sealed class ShotCgDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("resource", 0, Primary = true, Required = true)]
    public string Resource { get; set; } = string.Empty;
    [CommandParameter("view", 1, DefaultValue = "main")]
    public string View { get; set; } = "main";
    [CommandParameter("center", 2)] public Vector2? Center { get; set; }
    [CommandParameter("zoom", 3, Minimum = 0.001)] public double? Zoom { get; set; }
    [CommandParameter("rotation", 4)] public double? Rotation { get; set; }
}

[RuntimeCommand("shot", "use", Category = "Direction", Usage = "shot use <alias> [view:<id>] [via:<kind>] [over:<seconds>] [ease:<curve>] [join:<commit|settle>]")]
public sealed class ShotUseDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("alias", 0, Primary = true, Required = true)]
    public string Alias { get; set; } = string.Empty;
    [CommandParameter("view", 1)] public string? View { get; set; }
}

[RuntimeCommand("layout", Category = "Direction", Usage = "layout <single|split|pip> <slot:view> ...")]
public sealed class LayoutDirectionCommandArguments : DirectionTransitionCommandArguments
{
    [CommandParameter("layout", 0, Primary = true, Required = true)]
    public string Layout { get; set; } = "single";

    [CommandParameter("main", 1)] public string? Main { get; set; }
    [CommandParameter("inset", 2)] public string? Inset { get; set; }

    public IReadOnlyDictionary<string, string> Bindings
    {
        get
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["main"] = Main ?? "main",
            };
            if (Inset is not null) bindings["inset"] = Inset;
            return bindings;
        }
    }
}

[RuntimeCommand("beat", "begin", Category = "Direction", Usage = "beat begin id:<id> [over:<seconds>] [ease:<curve>]")]
public sealed class BeatBeginDirectionCommandArguments
{
    [CommandParameter("id", 0, Required = true)] public string Id { get; set; } = string.Empty;
    [CommandParameter("over", 1, Minimum = 0)] public double? Over { get; set; }
    [CommandParameter("ease", 2)] public string? Ease { get; set; }
}

[RuntimeCommand("beat", "end", Category = "Direction", Usage = "beat end [join:<commit|settle>]")]
public sealed class BeatEndDirectionCommandArguments
{
    [CommandParameter("join", 0, DefaultValue = "settle")]
    public DirectionCompletionBarrier Join { get; set; }
}

[RuntimeCommand("beat", "join", Category = "Direction", Usage = "beat join <id>")]
public sealed class BeatJoinDirectionCommandArguments
{
    [CommandParameter("id", 0, Primary = true, Required = true)]
    public string Id { get; set; } = string.Empty;
}

[RuntimeCommand("beat", "finish", Category = "Direction", Usage = "beat finish <id>")]
public sealed class BeatFinishDirectionCommandArguments
{
    [CommandParameter("id", 0, Primary = true, Required = true)]
    public string Id { get; set; } = string.Empty;
}

[RuntimeCommand("beat", "abort", Category = "Direction", Usage = "beat abort")]
public sealed class BeatAbortDirectionCommandArguments;

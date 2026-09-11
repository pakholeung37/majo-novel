#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Timing;

namespace Majo.Runtime.RenderingTests;

public partial class LiveDissolveProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            await RunAsync();
            GD.Print("LIVE_DISSOLVE_OK: both inputs animate; finish, replacement and rollback release source.");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        using var host = new GodotDirectionRenderHost(this, new Vector2I(64, 64));
        await host.InitializeAsync(CancellationToken.None);
        var actor = new ColorRect { Position = new Vector2(-100, -100), Size = new Vector2(300, 300) };
        host.Actors.AddChild(actor);
        GodotDirectionRenderHost.AssignVisibilityLayer(actor, GodotDirectionRenderHost.SubjectVisibilityLayer);
        var state = DirectionState.CreateDefault();
        var backend = new GodotDirectionViewBackend(host);
        var plan = new DirectionPlanner().PlanDomain(state, new IDirectionDirective[]
        {
            new ViewPosePatch(ViewId.Main, DirectionValue.From(new DirectionPoint(46, 50)),
                default, default, new DirectionTransitionSpec(DirectionTransitionId.Dissolve, 30, EasingId.Linear)),
        });
        async Task<IPreparedDirectionComponent> Prepare() => await backend.PrepareAsync(plan, ViewId.Main,
            state.Views[ViewId.Main], plan.TargetState.Views[ViewId.Main], CancellationToken.None);
        var baseline = host.GetRenderResourceMetrics().RenderTargetCount;
        var transition = await Prepare();
        transition.Commit(default);
        var source = host.GetViewTransitionSource(ViewId.Main).Texture;
        Require(source is ViewportTexture, "Dissolve source must be a live viewport.");
        Require(host.ViewIds.Count == 1, "Transient source leaked into public View IDs.");
        Require(host.GetRenderResourceMetrics().RenderTargetCount == baseline + 5, "Missing source passes.");
        for (var frame = 0; frame < 12; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var expected = frame % 2 == 0 ? Colors.Red : Colors.Blue;
            actor.Color = expected;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            foreach (var texture in new[] { source!, host.RequireView(ViewId.Main).CompositeViewport.GetTexture() })
            {
                using var image = texture.GetImage();
                var actual = image.GetPixel(32, 32);
                Require(Math.Abs(actual.R - expected.R) < 0.1 && Math.Abs(actual.B - expected.B) < 0.1,
                    "A dissolve input froze or sampled the previous frame.");
            }
        }
        await transition.FinishAsync(CancellationToken.None);
        await transition.DisposeAsync();
        Require(host.GetRenderResourceMetrics().RenderTargetCount == baseline, "Finish leaked source.");
        transition = await Prepare();
        transition.Commit(default);
        var replacement = await Prepare();
        replacement.Commit(default);
        await transition.DisposeAsync();
        Require(host.GetRenderResourceMetrics().RenderTargetCount == baseline + 5, "Replacement leaked source.");
        replacement.Rollback(default);
        await replacement.DisposeAsync();
        Require(host.GetRenderResourceMetrics().RenderTargetCount == baseline, "Rollback leaked source.");
        await host.ShutdownAsync(CancellationToken.None);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateLiveVisualInputsAsync()
    {
        var events = new List<string>();
        using var texture = new PlaceholderTexture2D { Size = Vector2.One };
        var surface = new Sprite2D();
        GodotVisualInput Input(string name) => GodotVisualInput.Static(texture, new Rect2(0, 0, 1, 1), release: () => events.Add(name));
        var context = new VisualTransitionContext(surface, new DirectionTransitionSpec(DirectionTransitionId.Dissolve, 1));
        try
        {
            using (var effect = GodotVisualTransition.Prepare(new InputLifetimeDriver(events), context, Input("source"), Input("target")))
            { effect.Dispose(); effect.Dispose(); }
            Assert(string.Join(",", events) == "driver,source,target", "Visual inputs must release once, after the driver.");
            events.Clear();
            try { GodotVisualTransition.Prepare(new InputLifetimeDriver(events, true), context, Input("source"), Input("target")); }
            catch (InvalidOperationException) { }
            Assert(string.Join(",", events) == "source,target", "Driver preparation failure leaked visual inputs.");
            events.Clear();
            var faulty = GodotVisualInput.Static(texture, new Rect2(0, 0, 1, 1), release: () => { events.Add("source"); throw new InvalidOperationException(); });
            var cleanup = GodotVisualTransition.Prepare(new InputLifetimeDriver(events), context, faulty, Input("target"));
            try { cleanup.Dispose(); } catch (AggregateException) { }
            Assert(string.Join(",", events) == "driver,source,target", "A failing input release prevented other cleanup.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ExpectBackgroundFailureAsync<OperationCanceledException>(() => GodotVisualInput.WaitForFrameAsync(cancelled.Token));
        }
        finally { surface.Free(); }
        if (DisplayServer.GetName() != "headless") await ValidateLiveActorPixelsAsync();
    }

    private async Task ValidateLiveActorPixelsAsync()
    {
        var directory = ProjectSettings.GlobalizePath("res://artifacts/live-visual");
        Directory.CreateDirectory(directory);
        var viewport = new SubViewport { Size = new Vector2I(64, 64), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        var owner = new Node2D(); viewport.AddChild(owner);
        var source = new LiveVisualTestContent { Color = Colors.Red, Polygon = new[] { Vector2.Zero, new Vector2(64, 0), new Vector2(64, 64), new Vector2(0, 64) } };
        var target = new LiveVisualTestContent { Color = Colors.Blue, Polygon = source.Polygon };
        var service = new GodotActorAppearanceTransitionService();
        Sprite2D? overlay = null;
        try
        {
            using var prepared = await service.PrepareForCommitAsync(owner, source, target, new Rect2(0, 0, 64, 64),
                _ => { }, value => overlay = value, () => { }, () => { }, "target",
                new TransitionDriverRegistry().GetRequired(DirectionTransitionId.Dissolve), new AnimationSpec(1));
            prepared.Commit(); prepared.ApplyProgress(.5f);
            using (var frame = await ReadTransitionFrameAsync(viewport)) AssertBackgroundPixel(frame, new Color(.5f, 0, .5f), "Live actor initial dissolve");
            var sourceTicks = source.ProcessCount;
            var targetTicks = target.ProcessCount;
            source.Color = Colors.Green;
            using (var frame = await ReadTransitionFrameAsync(viewport))
            {
                AssertBackgroundPixel(frame, new Color(0, .5f, .5f), "Actor source stopped rendering during dissolve");
                frame.SavePng(Path.Combine(directory, "actor-source-live.png"));
            }
            target.Color = Colors.Red;
            using (var frame = await ReadTransitionFrameAsync(viewport))
            {
                AssertBackgroundPixel(frame, new Color(.5f, .5f, 0), "Actor target stopped rendering during dissolve");
                frame.SavePng(Path.Combine(directory, "actor-target-live.png"));
            }
            Assert(source.ProcessCount > sourceTicks && target.ProcessCount > targetTicks,
                "Actor input simulations must remain active while mixed.");
            Assert(overlay is not null, "Live actor overlay was not published.");
            prepared.Finish();
            Assert(overlay is null && !IsInstanceValid(source) && !IsInstanceValid(target), "Actor transition did not release its input trees.");
        }
        finally { viewport.QueueFree(); }
    }

    private sealed class InputLifetimeDriver(List<string> events, bool fail = false) : ITransitionDriver
    {
        public DirectionTransitionId Id => DirectionTransitionId.Dissolve;
        public IPreparedVisualTransition Prepare(VisualTransitionContext context)
        {
            if (fail) throw new InvalidOperationException("Injected preparation failure.");
            return new InputLifetimeEffect(events);
        }
    }
    private sealed class InputLifetimeEffect(List<string> events) : IPreparedVisualTransition
    {
        public void ApplyProgress(float progress) { }
        public void Dispose() => events.Add("driver");
    }
}

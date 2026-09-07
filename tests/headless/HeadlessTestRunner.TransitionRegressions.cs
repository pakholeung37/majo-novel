#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateTransitionRegressionsAsync()
    {
        await ValidateDissolvePixelsAsync();
        using var lifetime = new HeadlessDirectionRuntimeLifetime();
        using var frameClock = new GodotFrameClock(GetTree());
        await frameClock.InitializeAsync(CancellationToken.None);
        var runner = new OperationRunner(frameClock, lifetime);
        await runner.InitializeAsync(CancellationToken.None);
        var owner = new Node2D(); AddChild(owner);
        try
        {
            var failing = new EasingDefinition("fails-between-samples", t => t > .2 && t < .3 ? double.NaN : t).Validate();
            var finished = false;
            var operation = runner.Start(GodotTweenOperation.CreateProgressOperation(owner,
                new AnimationTiming(1), failing, _ => { }, () => finished = true));
            var tween = GetTree().GetProcessedTweens().Single();
            tween.Pause(); tween.CustomStep(.25);
            await AssertThrowsAsync<InvalidOperationException>(() => operation.Completion);
            Assert(operation.State == OperationState.Faulted && !finished,
                "A failing callable must fault the operation instead of committing its target.");

            using var red = SolidTexture(new Color(1, 0, 0, .5f));
            using var blue = SolidTexture(new Color(0, 0, 1, .5f));
            using var green = SolidTexture(Colors.Green);
            var loader = new TransitionTextureLoader(new Dictionary<string, Texture2D>
            { ["red"] = red, ["blue"] = blue, ["green"] = green });
            var registry = new TransitionDriverRegistry();
            var custom = new RecordingTransitionDriver(); registry.Register(custom);
            var view = new Sprite2D { Name = "RegressionActor", Centered = false };
            owner.AddChild(view);
            var actor = new CharacterActor(new ActorDefinition("RegressionActor", ActorFactoryIds.Character,
                "red", new Dictionary<string, string> { ["red"] = "red", ["blue"] = "blue", ["green"] = "green" }),
                view, runner, loader, new CharacterPositionMap(new ActorConfiguration()),
                BuiltInEasings.CreateDefaultRegistry(), registry);
            var baselineChildren = view.GetChildCount();
            var appearance = actor.ChangeAppearance("blue", new AnimationSpec(1), custom);
            var opacity = actor.ChangeOpacity(.5f, new AnimationSpec(1));
            await WaitForTransitionAsync(view);
            Assert(!appearance.Completion.IsCompleted && !opacity.Completion.IsCompleted,
                "Appearance and opacity must own independent operation slots.");
            actor.SetAppearance("green");
            await AssertThrowsAsync<OperationCanceledException>(() => appearance.Completion);
            Assert(actor.Appearance == "green" && ReferenceEquals(view.Texture, green),
                "Cancelling an appearance must not overwrite SetAppearance's new texture.");
            opacity.Finish(); await opacity.Completion;
            Assert(view.GetChildCount() == baselineChildren, "Cancelled appearance leaked capture nodes.");
            Assert(custom.Preparations == 1, "The selected actor driver was bypassed.");

            var first = actor.ChangeAppearance("red", new AnimationSpec(1));
            await WaitForTransitionAsync(view);
            var second = actor.ChangeAppearance("blue", new AnimationSpec(1));
            await first.Completion;
            await WaitForTransitionAsync(view);
            second.Finish(); await second.Completion;
            Assert(actor.Appearance == "blue" && actor.TargetAppearance == "blue" && view.GetChildCount() == baselineChildren,
                "Preemption failed to settle the newest appearance or release old inputs.");
            actor.Destroy();
        }
        finally
        {
            await runner.ShutdownAsync(CancellationToken.None);
            await frameClock.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }
        GD.Print("Transition lifecycle regressions passed.");
    }

    private async Task WaitForTransitionAsync(Sprite2D view)
    {
        for (var i = 0; i < 20; i++)
        {
            if (view.GetChildren().OfType<Sprite2D>().Any(child => child.Visible)) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        throw new InvalidOperationException("Appearance did not publish its prepared inputs.");
    }

    private static ImageTexture SolidTexture(Color color)
    {
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(color);
        return ImageTexture.CreateFromImage(image);
    }

    private async Task ValidateDissolvePixelsAsync()
    {
        var hostOwner = new Node(); AddChild(hostOwner);
        var host = new GodotDirectionRenderHost(hostOwner, new Vector2I(16, 16));
        await host.InitializeAsync(CancellationToken.None);
        Assert(host.ScreenTransitionSource.GetViewport() != host.ScreenViewport,
            "Screen effect must not sample the viewport that contains itself.");
        var mainView = host.GetOrCreateView(ViewId.Main);
        Assert(mainView.ContentViewport != mainView.TransitionSource.GetViewport(),
            "View effect must sample a separate camera-rendered input.");
        await host.ShutdownAsync(CancellationToken.None);
        hostOwner.QueueFree();
        if (DisplayServer.GetName() == "headless") return;

        var viewport = new SubViewport
        {
            Size = new Vector2I(4, 4),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        AddChild(viewport);
        var surface = new Sprite2D { Centered = false }; viewport.AddChild(surface);
        using var red = SolidTexture(Colors.Red);
        using var blue = SolidTexture(Colors.Blue);
        surface.Texture = blue; // Catches accidental multiplication by the carrier texture.
        var driver = new DissolveTransitionDriver();
        using (var prepared = driver.Prepare(new VisualTransitionContext(surface,
            new DirectionTransitionSpec(driver.Id, 1), red, blue)))
        {
            foreach (var progress in new[] { 0f, .25f, .5f, .75f, 1f })
            {
                prepared.ApplyProgress(progress);
                using var result = await ReadTransitionFrameAsync(viewport);
                AssertColor(result.GetPixel(1, 1), Colors.Red.Lerp(Colors.Blue, progress), "Dissolve pixel mismatch");
            }
        }
        using var translucent = SolidTexture(new Color(1, 0, 0, .5f));
        surface.Texture = translucent;
        using (var prepared = driver.Prepare(new VisualTransitionContext(surface,
            new DirectionTransitionSpec(driver.Id, 1), translucent, translucent)))
        {
            prepared.ApplyProgress(.5f);
            using var result = await ReadTransitionFrameAsync(viewport);
            AssertColor(result.GetPixel(1, 1), new Color(.5f, 0, 0, .5f), "Dissolve must apply texture alpha only once");
        }
        viewport.QueueFree();
        GD.Print("Dissolve pixel regressions passed with real rendering.");
    }

    private async Task<Image> ReadTransitionFrameAsync(SubViewport viewport)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return viewport.GetTexture().GetImage();
    }

    private static void AssertColor(Color actual, Color expected, string message) =>
        Assert(Math.Abs(actual.R - expected.R) <= 2f / 255 && Math.Abs(actual.G - expected.G) <= 2f / 255 &&
            Math.Abs(actual.B - expected.B) <= 2f / 255 && Math.Abs(actual.A - expected.A) <= 2f / 255,
            $"{message}: expected {expected}, got {actual}.");

    private sealed class TransitionTextureLoader(Dictionary<string, Texture2D> textures) : IActorTextureLoader
    {
        public Texture2D Load(string resourcePath) => textures[resourcePath];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingTransitionDriver : ITransitionDriver
    {
        public DirectionTransitionId Id => new("recording-dissolve");
        public TransitionDomains Domains => TransitionDomains.Actor;
        public int Preparations { get; private set; }
        public IPreparedVisualTransition Prepare(VisualTransitionContext context)
        {
            Preparations++;
            return new DissolveTransitionDriver().Prepare(context);
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateRenderedRestoreAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Visual restore verification requires a real renderer.");
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var evidence = ProjectSettings.GlobalizePath("res://build/restore-render-evidence");
        Directory.CreateDirectory(evidence);
        var owner = new Node { Name = "RenderedRestore" };
        AddChild(owner);
        var renderer = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await renderer.InitializeAsync(CancellationToken.None);
        var oldColor = new Color(0.8f, 0.15f, 0.55f);
        var nextColor = new Color(0.15f, 0.8f, 0.35f);
        var curtainColor = new Color(0.12f, 0.18f, 0.4f);
        const float curtainOpacity = 0.12f;
        PublishTarget(oldColor, nextColor,
            GD.Load<Texture2D>("res://tests/headless/fixtures/character_red.svg"));
        await RenderedFrameAsync();
        using (var initial = renderer.RetainRestoreOutput())
            await initial.WaitForTargetAsync(CancellationToken.None);
        await RenderedFrameAsync();
        var input = new InputManager(new FakeInputBackend());
        await input.InitializeAsync(CancellationToken.None);
        var contributor = new RenderedRestoreContributor();
        using var state = new StateManager([contributor], input,
            new RenderedStateBoundary(renderer));
        await state.InitializeAsync(CancellationToken.None);
        var timings = new List<double>();
        var frames = 0;
        var baselineNodes = 0;
        try
        {
            for (var iteration = 0; iteration < 200; iteration++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Texture2D? preparedActor = null;
                contributor.Prepare = async token =>
                {
                    // Deliberately clear underlying surfaces during candidate preparation.
                    // The retained output must remain valid while the candidate is cold.
                    await renderer.ResetAsync(token);
                    foreach (var actor in renderer.Actors.GetChildren()) actor.Free();
                    renderer.StageBackground.Color = Colors.Black;
                    preparedActor = GD.Load<Texture2D>(iteration % 2 == 0
                        ? "res://tests/headless/fixtures/character_blue.svg"
                        : "res://tests/headless/fixtures/character_red.svg");
                    await RenderedFrameAsync();
                    CheckRoot(oldColor, nextColor, iteration, "prepare");
                };
                contributor.Commit = () => PublishTarget(nextColor, oldColor,
                    preparedActor ?? throw new InvalidOperationException("Cold actor asset was not prepared."));
                contributor.Activate = async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await RenderedFrameAsync();
                    using (var target = renderer.ScreenViewport.GetTexture().GetImage())
                    {
                        CheckSplit(target, nextColor, oldColor,
                            "Target dependency chain has not rendered.");
                        CheckActor(target, nextColor,
                            "Target actor asset has not rendered through Stage/View/Screen.");
                    }
                    CheckRoot(oldColor, nextColor, iteration, "ready-covered");
                };
                await state.RestoreGameAsync(RenderedRestoreContributor.Target(iteration));
                timings.Add(watch.Elapsed.TotalMilliseconds);
                await RenderedFrameAsync();
                CheckRoot(nextColor, oldColor, iteration, "target");
                (oldColor, nextColor) = (nextColor, oldColor);
                var nodes = CountNodes(owner);
                if (iteration == 0) baselineNodes = nodes;
                Assert(nodes == baselineNodes, $"Restore leaked nodes: {baselineNodes} -> {nodes} at {iteration}.");
            }
            Assert(contributor.CreatedCandidates == 200 &&
                    contributor.DisposedCandidates == 200 &&
                    contributor.ActiveCandidates == 0 &&
                    contributor.MaximumActiveCandidates == 1,
                "State restore candidates were not released exactly once.");
            timings.Sort();
            var result = new
            {
                renderer = RenderingServer.GetRenderingDevice() is null ? "compatibility" : "rendering-device",
                iterations = 200,
                checkedFrames = frames,
                nodes = baselineNodes,
                candidatesCreated = contributor.CreatedCandidates,
                candidatesDisposed = contributor.DisposedCandidates,
                maximumActiveCandidates = contributor.MaximumActiveCandidates,
                p50Milliseconds = timings[timings.Count / 2],
                p95Milliseconds = timings[(int)(timings.Count * 0.95)],
                scope = "Full StateManager prepare/commit/presentation path with retained output and actual Stage/View/Screen rendering; synthetic cold prepare gap, split output, recreated secondary Stage/View surfaces",
            };
            File.WriteAllText(Path.Combine(evidence, "results.json"),
                System.Text.Json.JsonSerializer.Serialize(result));
            GD.Print($"Rendered restore evidence: {evidence}");
        }
        finally
        {
            await state.ShutdownAsync(CancellationToken.None);
            await input.ShutdownAsync(CancellationToken.None);
            await renderer.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }

        void PublishTarget(Color left, Color right, Texture2D actorTexture)
        {
            renderer.StageBackground.Color = left;
            renderer.Actors.AddChild(new Sprite2D
            {
                Name = "RestoreActor",
                Texture = actorTexture,
                Position = new Vector2(320, 180),
                Scale = new Vector2(0.15f, 0.15f),
            });
            var stage = new StageId("right");
            var view = new ViewId("right");
            var surface = renderer.GetOrCreateStage(stage);
            surface.Background.Color = right;
            renderer.PublishView(view, new ViewState(view, stage, ViewPose.Default), surface.Viewport.GetTexture());
            renderer.PublishScreen(new ScreenState(new LayoutId("split"),
                new Dictionary<LayoutSlotId, ViewId>
                {
                    [LayoutSlotId.Main] = ViewId.Main,
                    [new LayoutSlotId("inset")] = view,
                }));
            renderer.PublishEffects(new StableEffectState(
                new CurtainState(new DirectionColor(
                    curtainColor.R, curtainColor.G, curtainColor.B), curtainOpacity)));
        }

        void CheckRoot(Color left, Color right, int iteration, string stage)
        {
            using var image = GetViewport().GetTexture().GetImage();
            if (iteration < 2) image.SavePng(Path.Combine(evidence, $"{iteration:D3}-{stage}.png"));
            CheckSplit(image, Blend(left), Blend(right),
                $"Unexpected output at restore {iteration}, {stage}.");
            CheckActor(image, Blend(left),
                $"Actor disappeared at restore {iteration}, {stage}.");
            frames++;
        }

        Color Blend(Color value) => value.Lerp(curtainColor, curtainOpacity);
    }

    private sealed class RenderedStateBoundary(GodotDirectionRenderHost renderer)
        : IRuntimeRestoreBoundary, IRuntimeRestoreOutput
    {
        public IRetainedRestoreOutput RetainRestoreOutput() => renderer.RetainRestoreOutput();
        public Task CancelForRestoreAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RenderedRestoreContributor
        : IPreparedStateContributor, IStateMutationParticipant
    {
        public Func<CancellationToken, Task> Prepare { get; set; } = _ => Task.CompletedTask;
        public Action Commit { get; set; } = () => { };
        public Func<CancellationToken, Task> Activate { get; set; } = _ => Task.CompletedTask;
        public int CreatedCandidates { get; private set; }
        public int DisposedCandidates { get; private set; }
        public int ActiveCandidates { get; private set; }
        public int MaximumActiveCandidates { get; private set; }
        public string Key => "test.rendered-restore";
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public void BindStateMutationGate(StateMutationGate gate) { }
        public ValueTask<StateRecord> CaptureAsync(CancellationToken token) =>
            ValueTask.FromResult(Target(0).Records[Key]);
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = record.Payload.GetInt32();
            return ValueTask.CompletedTask;
        }
        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
            StateRecord? record, RestoreContext context, CancellationToken token)
        {
            await Prepare(token);
            CreatedCandidates++;
            ActiveCandidates++;
            MaximumActiveCandidates = Math.Max(MaximumActiveCandidates, ActiveCandidates);
            return new Candidate(this, Commit, Activate);
        }
        public static StateSnapshot Target(int iteration) => new(
            StateSnapshot.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            [new StateRecord("test.rendered-restore", StateScope.Game, 1,
                System.Text.Json.JsonSerializer.SerializeToElement(iteration))]);

        private sealed class Candidate(
            RenderedRestoreContributor owner,
            Action commit,
            Func<CancellationToken, Task> activate)
            : IPreparedStateRestore, IStateRestoreActivation
        {
            private bool disposed;
            public void Commit() => commit();
            public Task ActivateAsync(CancellationToken token) => activate(token);
            public ValueTask DisposeAsync()
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
                owner.DisposedCandidates++;
                owner.ActiveCandidates--;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static void CheckSplit(Image image, Color left, Color right, string failure)
    {
        Assert(!image.IsEmpty(), failure + " Image is empty.");
        foreach (var point in new[] { new Vector2(0.12f, 0.5f), new Vector2(0.75f, 0.5f) })
        {
            var expected = point.X < 0.5f ? left : right;
            var color = image.GetPixel((int)(image.GetWidth() * point.X), (int)(image.GetHeight() * point.Y));
            Assert(Math.Abs(color.R - expected.R) < 0.035f && Math.Abs(color.G - expected.G) < 0.035f &&
                Math.Abs(color.B - expected.B) < 0.035f, failure + $" Expected {expected}, got {color}.");
        }
    }

    private static void CheckActor(Image image, Color background, string failure)
    {
        var color = image.GetPixel((int)(image.GetWidth() * 0.25f), image.GetHeight() / 2);
        Assert(Math.Abs(color.R - background.R) > 0.08f ||
                Math.Abs(color.G - background.G) > 0.08f ||
                Math.Abs(color.B - background.B) > 0.08f,
            failure + $" Marker matched background {background}.");
    }

    private static int CountNodes(Node root) => 1 + root.GetChildren().Sum(CountNodes);

    private static async Task RenderedFrameAsync()
    {
        var completion = new TaskCompletionSource();
        var began = false;
        void Before() => began = true;
        void After() { if (began) completion.TrySetResult(); }
        RenderingServer.FramePreDraw += Before;
        RenderingServer.FramePostDraw += After;
        try { await completion.Task; }
        finally
        {
            RenderingServer.FramePreDraw -= Before;
            RenderingServer.FramePostDraw -= After;
        }
    }
}

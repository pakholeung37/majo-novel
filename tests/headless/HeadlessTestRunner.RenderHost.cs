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
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateRenderHostGpuAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Render host acceptance requires a real GPU renderer.");
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        foreach (var (name, test) in new (string, Func<Task>)[]
        {
            (nameof(ValidateDirectionRenderingAsync), ValidateDirectionRenderingAsync),
            (nameof(ValidateViewRenderDemandAsync), ValidateViewRenderDemandAsync),
            (nameof(ValidateCameraCutFrameAsync), ValidateCameraCutFrameAsync),
            (nameof(ValidateTransitionRegressionsAsync), ValidateTransitionRegressionsAsync),
            (nameof(ValidateLiveVisualInputsAsync), ValidateLiveVisualInputsAsync),
            (nameof(ValidateBackgroundAlphaAsync), ValidateBackgroundAlphaAsync),
            (nameof(ValidateBackgroundExtensionAsync), ValidateBackgroundExtensionAsync),
            (nameof(ValidateActorSlotsAsync), ValidateActorSlotsAsync),
            (nameof(ValidateLayeredCharacterAsync), ValidateLayeredCharacterAsync),
            (nameof(ValidateLayeredMasksAsync), ValidateLayeredMasksAsync),
            (nameof(ValidateRepeatedWallAsync), ValidateRepeatedWallAsync),
            (nameof(ValidateRenderOwnershipAsync), ValidateRenderOwnershipAsync),
            (nameof(ValidateRenderedRestoreAsync), ValidateRenderedRestoreAsync),
        })
        {
            await MeasureAsync(name, test);
            GD.Print($"MAJO_RENDER_CASE_OK {name}");
        }
    }

    private async Task ValidateRenderOwnershipAsync()
    {
        var evidence = ProjectSettings.GlobalizePath("res://build/render-host-ownership");
        Directory.CreateDirectory(evidence);
        var owner = new Node { Name = "RenderOwnershipProbe" };
        AddChild(owner);
        using var host = new GodotDirectionRenderHost(owner, new Vector2I(128, 72));
        await host.InitializeAsync(default);
        var stageViewport = host.StageViewport;
        var screenViewport = host.ScreenViewport;
        var main = host.RequireView(ViewId.Main);
        using var overlay = SolidTexture(Colors.Blue);
        try
        {
            host.StageBackground.Color = Colors.Red;
            host.PublishScreenTransition(overlay);
            host.SetScreenTransitionOpacity(.25f);
            host.PublishEffects(new StableEffectState(new CurtainState(new DirectionColor(0, 1, 0), .2f)));
            await RenderedFrameAsync();
            using var before = GetViewport().GetTexture().GetImage();
            var expected = before.GetPixel(before.GetWidth() / 2, before.GetHeight() / 2);
            AssertColor(expected, Colors.Red.Lerp(Colors.Blue, .25f).Lerp(Colors.Green, .2f),
                "Screen transition and curtain must be composed in order");

            using (var retained = host.RetainRestoreOutput())
            {
                var count = CountNodes(owner);
                AssertThrows<InvalidOperationException>(() => host.RetainRestoreOutput());
                Assert(CountNodes(owner) == count, "A duplicate retain created native nodes.");
                await host.ResetAsync(default);
                host.StageBackground.Color = Colors.Yellow;
                await RenderedFrameAsync();
                using var covered = GetViewport().GetTexture().GetImage();
                AssertColor(covered.GetPixel(covered.GetWidth() / 2, covered.GetHeight() / 2), expected,
                    "Retained output lost its transition or curtain during reset");
                covered.SavePng(Path.Combine(evidence, "retained-transition-curtain.png"));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(() => retained.WaitForTargetAsync(cancelled.Token));
                host.ActivateOutput();
                await retained.WaitForTargetAsync(default);
            }
            await RenderedFrameAsync();
            using (var target = GetViewport().GetTexture().GetImage())
            {
                AssertColor(target.GetPixel(target.GetWidth() / 2, target.GetHeight() / 2), Colors.Yellow,
                    "Releasing retained output did not reveal the committed frame");
                target.SavePng(Path.Combine(evidence, "released-target.png"));
            }

            var extraId = new ViewId("unactivated-transition");
            host.PublishView(extraId, new ViewState(extraId, StageId.Main, ViewPose.Default), host.GetStageTexture(StageId.Main));
            var driver = new RenderOwnershipDriver();
            var prepared = host.PrepareBackdropVisualTransition(StageId.Main,
                new DirectionTransitionSpec(DirectionTransitionId.Dissolve, 1), driver, activate: false);
            Assert(driver.Live == 2, "Each prepared camera must own its transition input.");
            host.ReleasePreparedView(extraId);
            Assert(driver.Live == 1, "Releasing a camera retained an unactivated transition input.");
            await host.ResetAsync(default);
            prepared.ApplyProgress(.5f);
            prepared.Activate();
            prepared.Dispose();
            Assert(driver.Live == 0 && driver.Released == 2,
                "Reset did not release prepared transition inputs exactly once.");
            using var stale = host.RetainRestoreOutput();
            await host.ShutdownAsync(default);
            await AssertThrowsAsync<ObjectDisposedException>(() => stale.WaitForTargetAsync(default));
            host.Dispose();
            host.Dispose();
        }
        finally
        {
            await host.ShutdownAsync(default);
            owner.QueueFree();
        }
        await RenderedFrameAsync();
        Assert(!GodotObject.IsInstanceValid(stageViewport) && !GodotObject.IsInstanceValid(screenViewport) &&
                !GodotObject.IsInstanceValid(main.Viewport), "Shutdown left native render surfaces alive.");

        var coldOwner = new Node();
        var cold = new GodotDirectionRenderHost(coldOwner, new Vector2I(128, 72));
        var coldView = cold.ViewViewport;
        cold.Dispose();
        cold.Dispose();
        await cold.ShutdownAsync(default);
        coldOwner.Free();
        await RenderedFrameAsync();
        Assert(!GodotObject.IsInstanceValid(coldView), "Disposal before initialization leaked a surface.");
    }

    private sealed class RenderOwnershipDriver : ITransitionDriver
    {
        public DirectionTransitionId Id => DirectionTransitionId.Dissolve;
        public int Live { get; private set; }
        public int Released { get; private set; }
        public IPreparedVisualTransition Prepare(VisualTransitionContext context)
        {
            Live++;
            return new Input(this);
        }
        private sealed class Input(RenderOwnershipDriver owner) : IPreparedVisualTransition
        {
            private bool disposed;
            public void ApplyProgress(float progress) => ObjectDisposedException.ThrowIf(disposed, this);
            public void Dispose()
            {
                if (disposed) throw new InvalidOperationException("Transition input was released twice.");
                disposed = true;
                owner.Live--;
                owner.Released++;
            }
        }
    }
}

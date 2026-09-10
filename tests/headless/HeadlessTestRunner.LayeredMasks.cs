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
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateLayeredMasksAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Layered mask pixel tests require a real renderer.");
        await ValidateStencilPixelsAsync();
        using var lifetime = new HeadlessDirectionRuntimeLifetime();
        using var frameClock = new GodotFrameClock(GetTree());
        await frameClock.InitializeAsync(CancellationToken.None);
        var runner = new OperationRunner(frameClock, lifetime);
        await runner.InitializeAsync(CancellationToken.None);
        var evidence = ProjectSettings.GlobalizePath("res://build/layered-mask-evidence");
        Directory.CreateDirectory(evidence);
        try
        {
            foreach (var name in new[] { "Miria", "Coco" })
            {
                var path = $"res://demos/manosaba/assets/characters/{name}/layered_character.json";
                var manifest = LayeredCharacterManifest.Parse(Godot.FileAccess.GetFileAsString(path));
                var loader = new TransitionTextureLoader(manifest.Layers.Select(layer => layer.Atlas).Distinct()
                    .ToDictionary(atlas => $"res://demos/manosaba/assets/characters/{name}/{atlas}",
                        atlas => GD.Load<Texture2D>($"res://demos/manosaba/assets/characters/{name}/{atlas}")));
                var viewport = new SubViewport
                {
                    Size = new Vector2I(640, 640), TransparentBg = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                };
                AddChild(viewport);
                var view = new Sprite2D();
                viewport.AddChild(view);
                using var actor = new LayeredCharacterActor(
                    new ActorDefinition(name, ActorFactoryIds.LayeredCharacter, "Default", new Dictionary<string, string>()),
                    view, runner, loader, new CharacterPositionMap(new ActorConfiguration()),
                    manifest, path, CharacterPositionPreset.Center);
                actor.SetPosition(new Vector2(320, name == "Miria" ? 1020 : 920));
                actor.SetScale(new Vector2(.7f, .7f));
                actor.SetVisible(true);
                var group = view.GetNode<CanvasGroup>("LayerComposite");
                foreach (var appearance in new[] { "Default", "Angry", "Fearful" })
                {
                    actor.SetAppearance(appearance);
                    using var expected = await ReadTransitionFrameAsync(viewport);
                    expected.SavePng($"{evidence}/{name}-{appearance}.png");

                    actor.SetAppearance("Default");
                    using var source = await ReadTransitionFrameAsync(viewport);
                    // Source and target use different eye sprites. Preparing the
                    // target must not mutate the source's shared mask material.
                    using (var prepared = await actor.PrepareAppearanceTransitionAsync(appearance,
                        new AnimationSpec(.1), new DissolveTransitionDriver()))
                    {
                        prepared.Commit();
                        prepared.ApplyProgress(0);
                        using var sourceEndpoint = await ReadTransitionFrameAsync(viewport);
                        AssertLayeredImagesClose(source, sourceEndpoint, $"{name}/{appearance} source endpoint");
                        prepared.ApplyProgress(1);
                        using var endpoint = await ReadTransitionFrameAsync(viewport);
                        AssertLayeredImagesClose(expected, endpoint, $"{name}/{appearance} endpoint");
                        prepared.Finish();
                    }
                }

                actor.SetAppearance("Default");
                // Reproduce the old behavior only in the evidence capture.
                foreach (var sprite in group.GetChildren().OfType<Sprite2D>())
                {
                    var layer = manifest.Layers.Single(layer => layer.Path.Replace('/', '_') == sprite.Name);
                    if (layer.Material.Contains("#Masked", StringComparison.Ordinal))
                        sprite.Material = layer.Material.Contains("Multiply", StringComparison.Ordinal)
                            ? LayeredCharacterActor.CreateAlphaAwareMultiplyMaterial() : null;
                }
                using var before = await ReadTransitionFrameAsync(viewport);
                before.SavePng($"{evidence}/{name}-before.png");
                actor.SetAppearance("Default");
                actor.Destroy();
                viewport.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        finally
        {
            await runner.ShutdownAsync(CancellationToken.None);
            await frameClock.ShutdownAsync(CancellationToken.None);
        }
        GD.Print($"Layered mask pixel and real-character endpoint regressions passed. Evidence: {evidence}");
    }

    private async Task ValidateStencilPixelsAsync()
    {
        using var dark = SolidTexture(new Color(.2f, .2f, .2f));
        using var yellow = SolidTexture(Colors.Yellow);
        using var alphaImage = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        alphaImage.Fill(new Color(.2f, .2f, .2f, 1));
        for (var y = 0; y < 4; y++) alphaImage.SetPixel(0, y, new Color(.2f, .2f, .2f, .1f));
        using var eyes = ImageTexture.CreateFromImage(alphaImage);
        var viewport = new SubViewport
        {
            Size = new Vector2I(16, 8), TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);
        var group = new CanvasGroup(); viewport.AddChild(group);
        var manifest = new LayeredCharacterManifest();
        Sprite2D Add(string name, Texture2D texture, Vector2 position, Vector2 scale, string material)
        {
            manifest.Layers.Add(new LayeredCharacterLayer { Path = name, Material = material });
            var sprite = new Sprite2D
            {
                Name = name, Texture = new AtlasTexture { Atlas = texture, Region = new Rect2(0, 0, 4, 4) },
                Centered = false, Position = position, Scale = scale,
            };
            group.AddChild(sprite);
            return sprite;
        }
        Add("Body", dark, Vector2.Zero, new Vector2(4, 2), "Naninovel_Default#Mask_Ref1_CutoffLow");
        var eye = Add("Eye", eyes, new Vector2(4, 0), Vector2.One, "Naninovel_Default#Mask_Ref2_CutoffLow");
        var otherEye = Add("OtherEye", eyes, new Vector2(10, 0), Vector2.One, "Naninovel_Default#Mask_Ref2_CutoffLow");
        otherEye.Visible = false;
        var overwrite = Add("Overwrite", dark, new Vector2(6, 0), new Vector2(.25f, 1), "Naninovel_Default#Mask_Ref1_CutoffLow");
        overwrite.Visible = false;
        var tint = Add("Tint", yellow, new Vector2(16, 8), new Vector2(-4, -2), "Naninovel_Default#Masked_Ref2");
        var masking = new LayeredCharacterMasking(manifest);
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
        {
            AssertColor(result.GetPixel(2, 2), new Color(.2f, .2f, .2f), "Hair outside Ref2 was overwritten");
            AssertColor(result.GetPixel(4, 2), new Color(.2f, .2f, .2f), "Below-cutoff eye alpha wrote stencil");
            AssertColor(result.GetPixel(6, 2), Colors.Yellow, "Ref2 did not clip transformed overlay to eye");
            AssertColor(result.GetPixel(12, 2), new Color(.2f, .2f, .2f), "Hidden eye wrote stencil");
        }
        eye.Position = new Vector2(8, 0); eye.RotationDegrees = 90;
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
            AssertColor(result.GetPixel(4, 2), Colors.Yellow, "Rotated writer sampled the wrong mask pixel");
        eye.Position = new Vector2(4, 0); eye.RotationDegrees = 0;
        overwrite.Visible = true;
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
            AssertColor(result.GetPixel(6, 2), new Color(.2f, .2f, .2f), "Later Ref1 did not replace Ref2");
        overwrite.Visible = false;
        eye.Visible = false; otherEye.Visible = true;
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
        {
            AssertColor(result.GetPixel(6, 2), new Color(.2f, .2f, .2f), "Old expression left a stale mask");
            AssertColor(result.GetPixel(12, 2), Colors.Yellow, "New expression failed to update mask");
        }
        otherEye.Visible = false;
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
            AssertColor(result.GetPixel(12, 2), new Color(.2f, .2f, .2f), "Missing mask must discard overlay");

        otherEye.Visible = true;
        manifest.Layers[^1] = new LayeredCharacterLayer { Path = "Tint", Material = "Naninovel_Multiply#Masked_Ref1" };
        masking = new LayeredCharacterMasking(manifest);
        masking.Apply(group);
        using (var result = await ReadTransitionFrameAsync(viewport))
        {
            AssertColor(result.GetPixel(2, 2), new Color(.2f, .2f, 0), "Masked multiply lost body shading");
            AssertColor(result.GetPixel(12, 2), new Color(.2f, .2f, 0), "Ref1 must also include Ref2");
        }
        viewport.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void AssertLayeredImagesClose(Image expected, Image actual, string message)
    {
        var a = expected.GetData(); var b = actual.GetData();
        Assert(a.Length == b.Length, $"{message}: image sizes differ");
        long difference = 0;
        for (var i = 0; i < a.Length; i++) difference += Math.Abs(a[i] - b[i]);
        Assert(difference / (double)a.Length < 1.5, $"{message}: flattened image differs by {difference / (double)a.Length:F3}");
    }
}

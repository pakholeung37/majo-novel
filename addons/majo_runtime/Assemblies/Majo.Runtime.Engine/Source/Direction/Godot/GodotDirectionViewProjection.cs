#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

// Projects logical camera state into raster passes and owns coverage warning suppression.
internal sealed class GodotDirectionViewProjection(IRuntimeLogger logger)
{
    private Vector2I logicalViewSize = new(1280, 720);
    private readonly HashSet<string> coverageWarnings = new(StringComparer.Ordinal);
    internal void SetLogicalSize(Vector2I size) => logicalViewSize = size;
    internal void ClearDiagnostics() => coverageWarnings.Clear();

    internal void Apply(GodotDirectionViewSurface surface, GodotDirectionStageSurface? stage)
    {
        var pose = surface.CurrentPose;
        var delta = surface.ModifierDelta;
        var size = (Vector2)logicalViewSize;
        var rasterScale = Transform2D.Identity.Scaled((Vector2)surface.Viewport.Size / size);
        var modifierBasis = new Transform2D(Mathf.DegToRad(delta.RotationDegreesOffset),
            new Vector2(delta.ZoomMultiplier, delta.ZoomMultiplier), 0, Vector2.Zero);
        var modifier = new Transform2D(modifierBasis.X, modifierBasis.Y,
            size / 2 - modifierBasis * (size / 2) + delta.CenterOffset);
        var view = surface.PublishedView;
        var distance = stage?.PublishedBackdrop?.Distance ?? 1;
        var projection = CameraProjectionCalculator.Calculate(
            pose,
            surface.CurrentParallax,
            surface.CurrentIdle,
            distance,
            new DirectionPoint(size.X, size.Y));
        surface.ContentViewport.CanvasTransform = rasterScale * modifier * ToGodot(projection.Subject, Vector2.Zero);
        surface.BackgroundViewport.CanvasTransform = rasterScale * modifier * ToGodot(projection.Background, Vector2.Zero);
        var transitionDistance = stage?.TransitionBackdrop?.Distance ?? distance;
        var transitionProjection = CameraProjectionCalculator.Calculate(
            pose,
            surface.CurrentParallax,
            surface.CurrentIdle,
            transitionDistance,
            new DirectionPoint(size.X, size.Y));
        surface.TransitionBackgroundViewport.CanvasTransform = rasterScale * modifier * ToGodot(
            transitionProjection.Background, Vector2.Zero);
        surface.Transform.Transform = modifier * ToGodot(projection.Subject, Vector2.Zero);
        if (view is not null && stage is not null)
        {
            CheckBackdropCoverage(surface, stage, stage.Backdrop, stage.PublishedBackdrop,
                FromGodot(modifier * ToGodot(projection.Background, Vector2.Zero)), Vector2.Zero, "target");
            CheckBackdropCoverage(surface, stage, stage.BackdropTransition, stage.TransitionBackdrop,
                FromGodot(modifier * ToGodot(transitionProjection.Background, Vector2.Zero)), Vector2.Zero, "source");
        }
    }

    private void CheckBackdropCoverage(
        GodotDirectionViewSurface view,
        GodotDirectionStageSurface stage,
        GodotDirectionBackdropVisual visual,
        BackdropState? state,
        DirectionAffineTransform projection,
        Vector2 modifierOffset,
        string input)
    {
        if (state is null || visual.Raster is null || !visual.Visible) return;
        if (visual.Raster.Coverage == BackgroundCoverage.Unknown)
        {
            var unknownKey = $"unknown/{view.Id}/{stage.Id}/{state.DefinitionId}";
            if (coverageWarnings.Add(unknownKey)) logger.Debug("direction.camera", "direction.camera.backdrop_coverage_unknown",
                $"Backdrop '{state.DefinitionId}' declares unknown coverage; rectangular coverage cannot be proven.");
            return;
        }
        var bounds = visual.VisibleBounds;
        var withModifier = projection with
        {
            TX = projection.TX + modifierOffset.X,
            TY = projection.TY + modifierOffset.Y,
        };
        var result = CameraCoverageCalculator.Evaluate(
            new DirectionPoint(bounds.Position.X, bounds.Position.Y),
            new DirectionPoint(bounds.Size.X, bounds.Size.Y),
            withModifier,
            new DirectionPoint(logicalViewSize.X, logicalViewSize.Y));
        var key = $"{view.Id.Value}/{stage.Id.Value}/{input}/{state.DefinitionId}";
        if (result.IsCovered)
        {
            coverageWarnings.Remove(key);
            return;
        }
        if (!coverageWarnings.Add(key)) return;
        logger.Warning(
            "direction.camera",
            "direction.camera.backdrop_coverage",
            $"Backdrop '{state.DefinitionId}' does not cover View '{view.Id}' after camera projection ({result.UncoveredEdges}).",
            properties: new Dictionary<string, object?>
            {
                ["view"] = view.Id.Value,
                ["stage"] = stage.Id.Value,
                ["definitionId"] = state.DefinitionId,
                ["transitionInput"] = input,
                ["uncoveredEdges"] = result.UncoveredEdges.ToString(),
            });
    }

    private static Transform2D ToGodot(DirectionAffineTransform value, Vector2 offset) => new(
        new Vector2(value.M11, value.M12),
        new Vector2(value.M21, value.M22),
        new Vector2(value.TX + offset.X, value.TY + offset.Y));

    private static DirectionAffineTransform FromGodot(Transform2D value) => new(
        value.X.X, value.X.Y, value.Y.X, value.Y.Y, value.Origin.X, value.Origin.Y);

}

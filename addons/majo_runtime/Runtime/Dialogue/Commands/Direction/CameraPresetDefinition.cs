#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Dialogue.Commands;

/// <summary>Shared literal preset resolution for live commands, node entry, and restore.</summary>
internal static class CameraPresetDefinition
{
    internal static ViewPosePatch Resolve(string node, DirectionCameraPreset definition,
        IDirectionScopeResolver scopes, CameraEnvironmentPresetCatalog environment)
    {
        var options = definition.Options;
        var parent = options?.TryGetValue("extends", out var baseName) == true
            ? scopes.ResolveCameraPreset(node, baseName) : null;
        bool Has(string key) => options is null || options.ContainsKey(key);
        var center = Has("center") ? scopes.ResolvePosition(node, PositionRefConverter.Instance.Parse(definition.Center))
            : parent?.Center.Value ?? new DirectionPoint(50, 50);
        var zoom = Has("zoom") ? definition.Zoom : parent?.Zoom.Value ?? 1;
        var rotation = Has("rotation") ? definition.Rotation : parent?.RotationDegrees.Value ?? 0;
        var dolly = Has("dolly") ? environment.ResolveDolly(definition.Dolly)
            : parent?.Dolly.Value ?? 0;
        new ViewPose(center, zoom, rotation) { Dolly = dolly }.Validate();
        var parallax = parent?.Parallax ?? ViewParallaxState.Depth;
        var backgroundOffset = parent?.BackgroundOffset.Value ?? default;
        var backgroundScale = parent?.BackgroundScale.Value ?? 1;
        if (options is not null)
        {
            if (options.TryGetValue("backgroundOffset", out var offset))
                backgroundOffset = scopes.ResolvePosition(node, PositionRefConverter.Instance.Parse(offset));
            if (options.TryGetValue("backgroundScale", out var scale))
                backgroundScale = float.Parse(scale, CultureInfo.InvariantCulture);
            if (options.ContainsKey("parallaxStrength") || options.ContainsKey("parallaxAnchor") || options.ContainsKey("parallaxLimit"))
                parallax = parallax with { UseDepthProjection = false };
            if (options.TryGetValue("parallax", out var preset)) parallax = environment.ResolveParallax(preset);
            if (options.TryGetValue("parallaxStrength", out var strength))
                parallax = parallax with { Strength = float.Parse(strength, CultureInfo.InvariantCulture) };
            if (options.TryGetValue("parallaxAnchor", out var anchor))
                parallax = parallax with { Anchor = scopes.ResolvePosition(node, PositionRefConverter.Instance.Parse(anchor)) };
            if (options.TryGetValue("parallaxLimit", out var limit))
                parallax = parallax with { LimitPercent = scopes.ResolvePosition(node, PositionRefConverter.Instance.Parse(limit)) };
        }
        new ViewPose(center, zoom, rotation) { Dolly = dolly, BackgroundOffset = backgroundOffset, BackgroundScale = backgroundScale }.Validate();
        return new ViewPosePatch(ViewId.Main, DirectionValue.From(center), DirectionValue.From(zoom),
            DirectionValue.From(rotation), DirectionTransitionSpec.Cut)
        {
            Dolly = DirectionValue.From(dolly), Parallax = parallax.Validate(),
            BackgroundOffset = DirectionValue.From(backgroundOffset), BackgroundScale = DirectionValue.From(backgroundScale),
        };
    }

    internal static DirectionCameraPreset Parse(string name, IReadOnlyDictionary<string, string> options) => new(
        name, options.GetValueOrDefault("center", "50,50"),
        float.Parse(options.GetValueOrDefault("zoom", "1"), CultureInfo.InvariantCulture),
        float.Parse(options.GetValueOrDefault("rotation", "0"), CultureInfo.InvariantCulture),
        options.GetValueOrDefault("dolly", "neutral")) { Options = options };
}

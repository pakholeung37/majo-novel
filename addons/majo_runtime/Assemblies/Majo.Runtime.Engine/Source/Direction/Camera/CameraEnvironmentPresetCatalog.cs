#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Camera;

public sealed class CameraEnvironmentPresetCatalog : IRuntimeService
{
    private readonly Dictionary<string, float> distances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> dollies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViewParallaxState> parallax = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViewIdleState> idle = new(StringComparer.OrdinalIgnoreCase);

    public CameraEnvironmentPresetCatalog(CameraEnvironmentConfiguration? configuration = null)
    {
        AddDistance("flat", 1); AddDistance("near", 2); AddDistance("far", 6);
        AddDolly("neutral", 0); AddDolly("near", 0.15f); AddDolly("back", -0.25f);
        AddParallax("off", ViewParallaxState.Off);
        AddParallax("subtle", new ViewParallaxState(0.25f, new(50, 50), new(1.5f, 1)));
        AddParallax("normal", new ViewParallaxState(0.5f, new(50, 50), new(3, 2)));
        AddParallax("strong", new ViewParallaxState(1, new(50, 50), new(6, 4)));
        AddIdle("off", ViewIdleState.Off);
        AddIdle("gentle", new ViewIdleState(new(0.15f, 0.08f), 12));
        if (configuration is null) return;
        foreach (var value in configuration.DistancePresets) AddDistance(value.Name, value.Distance);
        foreach (var value in configuration.DollyPresets) AddDolly(value.Name, value.Dolly);
        foreach (var value in configuration.ParallaxPresets)
            AddParallax(value.Name, new ViewParallaxState(value.Strength,
                new(value.AnchorPercent.X, value.AnchorPercent.Y),
                new(value.LimitPercent.X, value.LimitPercent.Y)));
        foreach (var value in configuration.IdlePresets)
            AddIdle(value.Name, new ViewIdleState(
                new(value.AmplitudePercent.X, value.AmplitudePercent.Y), value.PeriodSeconds));
    }

    public void AddDistance(string name, float value) => Add(distances, name, ValidateDistance(value), "distance");
    public void AddDolly(string name, float value) => Add(dollies, name, ValidateDolly(value), "dolly");
    public void AddParallax(string name, ViewParallaxState value) => Add(parallax, name, value.Validate(), "parallax");
    public void AddIdle(string name, ViewIdleState value) => Add(idle, name, value.Validate(), "idle");

    public float ResolveDistance(string name) => Resolve(distances, name, "distance");
    public float ResolveDolly(string name) => float.TryParse(name,
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
        ? ValidateDolly(value) : Resolve(dollies, name, "dolly");
    public ViewParallaxState ResolveParallax(string name) => Resolve(parallax, name, "parallax");
    public ViewIdleState ResolveIdle(string name) => Resolve(idle, name, "idle");

    private static void Add<T>(Dictionary<string, T> values, string name, T value, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!values.TryAdd(name.Trim(), value))
            throw new ArgumentException($"Camera {category} preset '{name}' is already registered.", nameof(name));
    }

    private static T Resolve<T>(Dictionary<string, T> values, string name, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return values.TryGetValue(name.Trim(), out var value) ? value :
            throw new DirectionReferenceException($"Unknown camera {category} preset '{name}'.");
    }

    private static float ValidateDistance(float value)
    {
        new BackdropState("validation", "background", "Default") { Distance = value }.Validate();
        return value;
    }

    private static float ValidateDolly(float value)
    {
        (ViewPose.Default with { Dolly = value }).Validate();
        return value;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

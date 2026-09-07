#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

public interface ITransitionDriverRegistry : IRuntimeService
{
    void Register(ITransitionDriver driver);

    bool TryGet(DirectionTransitionId id, out ITransitionDriver? driver);

    ITransitionDriver GetRequired(DirectionTransitionId id);

    bool Contains(DirectionTransitionId id);

    bool Supports(DirectionTransitionId id, TransitionDomains domain);

    void Seal();

    void RegisterShader(
        DirectionTransitionId id,
        Shader shader,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false);

    void RegisterShaderMaterial(
        DirectionTransitionId id,
        ShaderMaterial material,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false);
}

public sealed class TransitionDriverRegistry : ITransitionDriverRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<DirectionTransitionId, ITransitionDriver> drivers = new();
    private bool isSealed;

    public TransitionDriverRegistry(bool registerDefaults = true)
    {
        if (registerDefaults)
        {
            Register(new CutTransitionDriver());
            Register(new FadeTransitionDriver());
            Register(new DissolveTransitionDriver());
        }
    }

    public void Register(ITransitionDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (driver.Id.IsEmpty)
            throw new ArgumentException("Transition driver ID cannot be empty.", nameof(driver));
        lock (gate)
        {
            if (isSealed)
                throw new InvalidOperationException("Transition driver registry is sealed.");
            if (drivers.ContainsKey(driver.Id))
                throw new InvalidOperationException(
                    $"Transition driver '{driver.Id}' is already registered.");
            drivers.Add(driver.Id, driver);
        }
    }

    public bool TryGet(DirectionTransitionId id, out ITransitionDriver? driver)
    {
        lock (gate)
        {
            return drivers.TryGetValue(id, out driver);
        }
    }

    public ITransitionDriver GetRequired(DirectionTransitionId id)
    {
        if (TryGet(id, out var driver) && driver is not null)
        {
            return driver;
        }

        throw new KeyNotFoundException($"No transition driver registered for '{id}'.");
    }

    public bool Contains(DirectionTransitionId id)
    {
        lock (gate)
        {
            return drivers.ContainsKey(id);
        }
    }

    public bool Supports(DirectionTransitionId id, TransitionDomains domain)
    {
        lock (gate)
        {
            return drivers.TryGetValue(id, out var driver) &&
                (driver.Domains & domain) == domain;
        }
    }

    public void Seal()
    {
        lock (gate) isSealed = true;
    }

    public void RegisterShader(
        DirectionTransitionId id,
        Shader shader,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
    {
        Register(new ShaderTransitionDriver(id, shader, ruleTexture, smoothness, invert));
    }

    public void RegisterShaderMaterial(
        DirectionTransitionId id,
        ShaderMaterial material,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
    {
        Register(new ShaderTransitionDriver(id, material, ruleTexture, smoothness, invert));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

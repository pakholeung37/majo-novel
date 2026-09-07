#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

public sealed class FadeTransitionDriver : ITransitionDriver
{
    public DirectionTransitionId Id => DirectionTransitionId.Fade;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;
    public bool SupportsCustomEasing => true;

    public IPreparedVisualTransition Prepare(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var initialAlpha = context.Surface.Modulate.A;
        return new FadePreparedTransition(context.Surface, initialAlpha, context.IsActive);
    }

    private sealed class FadePreparedTransition : IPreparedVisualTransition
    {
        private readonly CanvasItem surface;
        private readonly float initialAlpha;
        private readonly Func<bool>? isActive;

        public FadePreparedTransition(CanvasItem surface, float initialAlpha, Func<bool>? isActive)
        {
            this.surface = surface;
            this.initialAlpha = initialAlpha;
            this.isActive = isActive;
        }

        public void ApplyProgress(float progress)
        {
            if (isActive?.Invoke() != false && GodotObject.IsInstanceValid(surface))
            {
                surface.Modulate = new Color(
                    surface.Modulate,
                    (1.0f - Math.Clamp(progress, 0.0f, 1.0f)) * initialAlpha);
            }
        }

        public void Dispose() { }
    }
}

public sealed class DissolveTransitionDriver : ITransitionDriver
{
    private const string DefaultShaderPath =
        "res://addons/majo_runtime/Runtime/Direction/Shaders/DissolveTransition.gdshader";

    private readonly Shader? shader;

    public DissolveTransitionDriver(Shader? shader = null)
    {
        this.shader = shader;
    }

    public DirectionTransitionId Id => DirectionTransitionId.Dissolve;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;
    public bool SupportsCustomEasing => true;

    public IPreparedVisualTransition Prepare(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SourceTexture is null || context.TargetTexture is null)
            throw new DirectionException("Dissolve requires both source and target visual inputs.");
        var resolvedShader = shader ?? GD.Load<Shader>(DefaultShaderPath)
            ?? throw new DirectionException($"Dissolve shader is missing: {DefaultShaderPath}");
        var material = new ShaderMaterial { Shader = resolvedShader };
        material.SetShaderParameter("source_image", context.SourceTexture);
        material.SetShaderParameter("target_image", context.TargetTexture);
        material.SetShaderParameter("source_is_premultiplied", context.SourceIsPremultiplied);
        material.SetShaderParameter("target_is_premultiplied", context.TargetIsPremultiplied);
        material.SetShaderParameter("progress", 0f);
        context.Surface.Material = material;
        return new DissolvePreparedTransition(context.Surface, material, context.IsActive);
    }

    private sealed class DissolvePreparedTransition : IPreparedVisualTransition
    {
        private readonly CanvasItem surface;
        private readonly ShaderMaterial material;
        private readonly Func<bool>? isActive;
        private bool disposed;

        public DissolvePreparedTransition(
            CanvasItem surface,
            ShaderMaterial material,
            Func<bool>? isActive)
        {
            this.surface = surface;
            this.material = material;
            this.isActive = isActive;
        }

        public void ApplyProgress(float progress)
        {
            if (disposed) return;
            if (isActive?.Invoke() != false && GodotObject.IsInstanceValid(surface))
            {
                material.SetShaderParameter("progress", Math.Clamp(progress, 0.0f, 1.0f));
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (GodotObject.IsInstanceValid(surface) && ReferenceEquals(surface.Material, material))
            {
                surface.Material = null;
            }
            material.Dispose();
        }
    }

}

public sealed class CutTransitionDriver : ITransitionDriver
{
    public DirectionTransitionId Id => DirectionTransitionId.Cut;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;
    public bool SupportsCustomEasing => true;

    public IPreparedVisualTransition Prepare(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new CutPreparedTransition(context.Surface);
    }

    private sealed class CutPreparedTransition : IPreparedVisualTransition
    {
        private readonly CanvasItem surface;

        public CutPreparedTransition(CanvasItem surface)
        {
            this.surface = surface;
        }

        public void ApplyProgress(float progress)
        {
            if (GodotObject.IsInstanceValid(surface))
            {
                surface.Modulate = new Color(surface.Modulate, 0f);
            }
        }

        public void Dispose() { }
    }
}

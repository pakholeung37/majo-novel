#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

public sealed class ShaderTransitionDriver : ITransitionDriver
{
    private readonly ShaderMaterial materialTemplate;
    private readonly Texture2D? ruleTexture;
    private readonly float smoothness;
    private readonly bool invert;

    public ShaderTransitionDriver(
        DirectionTransitionId id,
        ShaderMaterial materialTemplate,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
    {
        Id = id;
        this.materialTemplate = materialTemplate ?? throw new ArgumentNullException(nameof(materialTemplate));
        this.ruleTexture = ruleTexture;
        this.smoothness = smoothness;
        this.invert = invert;
    }

    public ShaderTransitionDriver(
        DirectionTransitionId id,
        Shader shader,
        Texture2D? ruleTexture = null,
        float smoothness = 0.1f,
        bool invert = false)
        : this(
            id,
            new ShaderMaterial { Shader = shader ?? throw new ArgumentNullException(nameof(shader)) },
            ruleTexture,
            smoothness,
            invert)
    {
    }

    public DirectionTransitionId Id { get; }

    public bool SupportsCustomEasing => true;

    public IPreparedVisualTransition Prepare(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var material = (ShaderMaterial)materialTemplate.Duplicate();
        material.SetShaderParameter("progress", 0.0f);
        if (ruleTexture is not null)
        {
            material.SetShaderParameter("rule_texture", ruleTexture);
        }

        material.SetShaderParameter("smoothness", smoothness);
        material.SetShaderParameter("invert", invert);
        material.SetShaderParameter("source_is_premultiplied", context.SourceIsPremultiplied);
        material.SetShaderParameter("target_is_premultiplied", context.TargetIsPremultiplied);
        if (context.SourceTexture is not null)
        {
            material.SetShaderParameter("source_image", context.SourceTexture);
        }

        if (context.TargetTexture is not null)
        {
            material.SetShaderParameter("target_image", context.TargetTexture);
        }

        context.Surface.Material = material;
        return new ShaderPreparedTransition(context.Surface, material, context.IsActive);
    }

    private sealed class ShaderPreparedTransition : IPreparedVisualTransition
    {
        private readonly CanvasItem surface;
        private readonly ShaderMaterial material;
        private readonly Func<bool>? isActive;
        private bool disposed;

        public ShaderPreparedTransition(CanvasItem surface, ShaderMaterial material, Func<bool>? isActive)
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
